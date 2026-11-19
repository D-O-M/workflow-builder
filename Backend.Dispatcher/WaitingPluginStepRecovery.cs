using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Services.EngineManagement;
using Backend.Services.PluginRuntimeStatus;
using Backend.Services.ProcessStepQueue;

namespace Backend.Dispatcher;

/// <summary>Resumes waits using durable Step state even when no Engine is running.</summary>
public sealed class WaitingPluginStepRecovery(
    IProcessStepRepository steps,
    ITaskDefinitionRepository catalog,
    IPluginTaskVersionRuntimeStatusStore runtime,
    IEnginePolicyReader policies,
    IProcessStepDispatcher dispatcher,
    PluginRuntimeStatusOptions statusOptions)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // Waits span partitions and may be much older than normal dispatch repair windows.
        // Stream pages, and avoid touching SQL or runtime observations while the fleet is idle.
        RecoverySnapshot? snapshot = null;
        await foreach (var step in steps.GetPluginWaitsAsync(cancellationToken))
        {
            var wait = step.PluginWait!;
            if (!wait.WakeRequested)
            {
                snapshot ??= await LoadSnapshotAsync(cancellationToken);
                if (!snapshot.CanWake(wait))
                {
                    continue;
                }

                // Clear the previous intent and reserve a generation in one conditional write.
                // A crash here leaves WakeRequested visible to the next pass.
                step.DispatchGeneration = checked(step.DispatchGeneration + 1);
                step.PluginWait = wait with { WakeRequested = true, PolicyRevision = snapshot.Policy.Revision };
                step.UpdatedAt = DateTimeOffset.UtcNow;
                step.Dispatch = ProcessStepDispatchService.CreatePendingDispatchState(step.UpdatedAt.Value);
                if (!await steps.TryUpdateProcessStep(step, cancellationToken))
                {
                    continue;
                }
            }

            // Generation-specific intent/message IDs are stable across retries of this wake.
            await dispatcher.TryDispatchAsync(step, cancellationToken);
        }
    }

    private async Task<RecoverySnapshot> LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        var policy = await policies.ReadAsync(cancellationToken);
        var versions = await catalog.GetAll(cancellationToken);
        var observations = await runtime.GetFreshByTaskVersionIdsAsync(
            versions.Select(version => version.Id).ToArray(),
            DateTimeOffset.UtcNow.Subtract(statusOptions.StaleAfter),
            cancellationToken);
        var enabledLanes = versions.Where(version => !policy.IsDisabled(version.TaskDefinitionId, version.Id))
            .ToLookup(version => (version.TaskDefinitionId, version.Version.Major, version.Version.Minor), version => version.Id);
        return new RecoverySnapshot(policy, enabledLanes,
            observations.ToLookup(observation => observation.TaskVersionId, observation => observation.CurrentStatus));
    }

    private sealed record RecoverySnapshot(
        EnginePolicySnapshot Policy,
        ILookup<(int, int, int), int> EnabledLanes,
        ILookup<int, PluginTaskVersionRuntimeStatus> Observations)
    {
        public bool CanWake(ProcessStepPluginWait wait)
        {
            var patches = EnabledLanes[(wait.TaskDefinitionId, wait.Major, wait.Minor)];
            if (!patches.Any())
            {
                return false;
            }

            var observations = patches.SelectMany(patch => Observations[patch]).ToArray();
            // A newer policy can restore eligibility before the first replica reports in.
            // Periodically probe a cold lane as external capabilities or installed bundles can change.
            return Policy.Revision > wait.PolicyRevision ||
                observations.Contains(PluginTaskVersionRuntimeStatus.Runnable) ||
                (observations.Length == 0 && DateTimeOffset.UtcNow - wait.CheckedAt >= TimeSpan.FromMinutes(5));
        }
    }
}

public sealed class WaitingPluginStepWorker(
    IServiceScopeFactory scopes, ILogger<WaitingPluginStepWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<WaitingPluginStepRecovery>().RunAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Could not resume waiting Plugin Steps. The next pass will retry.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
