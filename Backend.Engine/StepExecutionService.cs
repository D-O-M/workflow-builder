using System.Collections.Concurrent;
using System.Diagnostics;
using Backend.Plugins.Communication;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Engine;

public enum StepStartDisposition
{
    Started,
    AlreadySettled,
    Deferred
}

public sealed record StepStartResult(
    StepStartDisposition Disposition)
{
    public static StepStartResult Started { get; } = new(StepStartDisposition.Started);
    public static StepStartResult AlreadySettled { get; } = new(StepStartDisposition.AlreadySettled);
    public static StepStartResult Deferred { get; } = new(StepStartDisposition.Deferred);
}

public interface IStepExecutionService
{
    Task<StepStartResult> StartPluginStepAsync(
        StepExecutionContext context,
        CancellationToken cancellationToken);

    Task HandlePluginResultAsync(
        PluginExecutionResult executionResult,
        CancellationToken cancellationToken);
}

public sealed class StepExecutionService : IStepExecutionService, IPluginAgentHealthDecisionHandler
{
    private readonly IAgentManager _agentManager;
    private readonly ConcurrentDictionary<Guid, StepExecutionContext> _activeSteps = new();
    private readonly PluginRuntimeCatalog _pluginRuntimeCatalog;
    private readonly IPluginRuntimeReloader _pluginRuntimeReloader;
    private readonly ProcessLifecycleTelemetry _processLifecycleTelemetry;
    private readonly PluginRuntimeAgentTelemetry _pluginRuntimeAgentTelemetry;
    private readonly ActiveStepLockRenewalService _lockRenewalService;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly StepClaimService _stepClaimService;
    private readonly ILogger<StepExecutionService> _logger;

    public StepExecutionService(
        IAgentManager agentManager,
        PluginRuntimeCatalog pluginRuntimeCatalog,
        IPluginRuntimeReloader pluginRuntimeReloader,
        ProcessLifecycleTelemetry processLifecycleTelemetry,
        PluginRuntimeAgentTelemetry pluginRuntimeAgentTelemetry,
        ActiveStepLockRenewalService lockRenewalService,
        IServiceScopeFactory serviceScopeFactory,
        StepClaimService stepClaimService,
        ILogger<StepExecutionService> logger)
    {
        _agentManager = agentManager;
        _pluginRuntimeCatalog = pluginRuntimeCatalog;
        _pluginRuntimeReloader = pluginRuntimeReloader;
        _processLifecycleTelemetry = processLifecycleTelemetry;
        _pluginRuntimeAgentTelemetry = pluginRuntimeAgentTelemetry;
        _lockRenewalService = lockRenewalService;
        _serviceScopeFactory = serviceScopeFactory;
        _stepClaimService = stepClaimService;
        _logger = logger;
    }

    public async Task<StepStartResult> StartPluginStepAsync(
        StepExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (!await TryResolvePluginRuntimeAsync(context, cancellationToken))
        {
            return StepStartResult.Deferred;
        }

        if (!_agentManager.TryAcquireStepSlot(context.ResolvedTaskVersionId))
        {
            return StepStartResult.Deferred;
        }

        StepClaimResult claim;
        try
        {
            claim = await _stepClaimService.TryClaimStepAsync(context, cancellationToken);
        }
        catch
        {
            _agentManager.ReleaseStepSlot(context.ResolvedTaskVersionId);
            throw;
        }

        if (!claim.StepClaimed)
        {
            _agentManager.ReleaseStepSlot(context.ResolvedTaskVersionId);
            await context.CompleteMessageAsync(cancellationToken);
            return StepStartResult.AlreadySettled;
        }

        if (!_activeSteps.TryAdd(context.Id, context))
        {
            _agentManager.ReleaseStepSlot(context.ResolvedTaskVersionId);
            await _stepClaimService.ResetStepToPendingAsync(context, cancellationToken);
            throw new InvalidOperationException($"A process step with id '{context.Id}' is already active.");
        }

        var lockRenewal = _lockRenewalService.Start(context);
        if (lockRenewal is not null)
        {
            context.AttachLockRenewal(lockRenewal);
        }

        bool queued;
        var pluginQueuedAt = DateTimeOffset.UtcNow;
        var enqueueStopwatch = Stopwatch.StartNew();
        var previousActivity = Activity.Current;
        context.ExecutionActivity = _processLifecycleTelemetry.StartStepActivity(context);
        try
        {
            queued = await _agentManager.GetAgent(context.ResolvedTaskVersionId)
                .QueueJob(
                    context.ToInvocationEnvelope(
                        InvocationTelemetryContext.FromActivity(context.ExecutionActivity ?? Activity.Current)),
                    cancellationToken);
        }
        catch
        {
            await context.StopLockRenewalAsync();
            _activeSteps.TryRemove(context.Id, out _);
            _agentManager.ReleaseStepSlot(context.ResolvedTaskVersionId);
            await _stepClaimService.ResetStepToPendingAsync(context, cancellationToken);
            _processLifecycleTelemetry.StopStepActivity(context, result: null, exception: null);
            throw;
        }
        finally
        {
            enqueueStopwatch.Stop();
            Activity.Current = previousActivity;
        }

        _processLifecycleTelemetry.RecordAgentEnqueue(context, enqueueStopwatch.Elapsed, queued);

        if (!queued)
        {
            await context.StopLockRenewalAsync();
            _activeSteps.TryRemove(context.Id, out _);
            _agentManager.ReleaseStepSlot(context.ResolvedTaskVersionId);
            await _stepClaimService.ResetStepToPendingAsync(context, cancellationToken);
            _processLifecycleTelemetry.StopStepActivity(context, result: null, exception: null);
            return StepStartResult.Deferred;
        }

        context.PluginQueuedAt = pluginQueuedAt;

        previousActivity = Activity.Current;
        Activity.Current = context.ExecutionActivity ?? previousActivity;
        try
        {
            _processLifecycleTelemetry.LogStepStarted(
                context,
                context.StartedAt ?? DateTimeOffset.UtcNow);

            if (claim.ProcessStarted)
            {
                _processLifecycleTelemetry.LogProcessStarted(
                    context,
                    context.StartedAt ?? DateTimeOffset.UtcNow);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Queued process step {BlueprintNodeId} for process {ProcessId} but failed to emit lifecycle telemetry.",
                context.BlueprintNodeId,
                context.ProcessId);
        }
        finally
        {
            Activity.Current = previousActivity;
        }

        return StepStartResult.Started;
    }

    public async Task HandlePluginResultAsync(
        PluginExecutionResult executionResult,
        CancellationToken cancellationToken)
    {
        if (!_activeSteps.TryGetValue(executionResult.JobId, out var context))
        {
            _logger.LogWarning(
                "Received a completion result for unknown process step {ProcessStepId}.",
                executionResult.JobId);
            return;
        }

        var previousActivity = Activity.Current;
        Activity.Current = context.ExecutionActivity ?? previousActivity;
        Exception? completionException = null;
        try
        {
            context.StartedAt = executionResult.StartedAt;
            context.CompletedAt = executionResult.CompletedAt;
            context.OutputChannelsPopulated = [.. executionResult.OutputData.Keys];
            if (context.PluginQueuedAt is DateTimeOffset pluginQueuedAt)
            {
                _processLifecycleTelemetry.RecordPluginStartDelay(
                    context,
                    executionResult.StartedAt - pluginQueuedAt);
            }

            _processLifecycleTelemetry.RecordPluginPhaseTimings(context, executionResult.TimingsMs);
            _processLifecycleTelemetry.RecordRecordedStepDuration(context, executionResult.CompletedAt);

            var finalizationStopwatch = Stopwatch.StartNew();
            await using var scope = _serviceScopeFactory.CreateAsyncScope();
            var finalizationService = scope.ServiceProvider.GetRequiredService<IStepFinalizationService>();
            if (executionResult.Succeeded)
            {
                await finalizationService.FinalizeSucceededStepAsync(context, executionResult, cancellationToken);
            }
            else
            {
                await finalizationService.FinalizeFailedStepAsync(context, executionResult, cancellationToken);
            }

            finalizationStopwatch.Stop();
            _processLifecycleTelemetry.RecordFinalization(
                context,
                finalizationStopwatch.Elapsed,
                executionResult.Succeeded);
        }
        catch (Exception ex)
        {
            completionException = ex;
            _logger.LogError(
                ex,
                "Failed to finalize process step {BlueprintNodeId} for process {ProcessId}.",
                context.BlueprintNodeId,
                context.ProcessId);

            try
            {
                await context.AbandonMessageAsync(cancellationToken);
            }
            catch (Exception abandonEx)
            {
                _logger.LogError(
                    abandonEx,
                    "Failed to abandon queue message for process step {BlueprintNodeId} after finalization failure.",
                    context.BlueprintNodeId);
            }
        }
        finally
        {
            await context.StopLockRenewalAsync();
            _processLifecycleTelemetry.StopStepActivity(context, executionResult, completionException);
            Activity.Current = previousActivity;
            _activeSteps.TryRemove(context.Id, out _);
            _agentManager.ReleaseStepSlot(context.ResolvedTaskVersionId);
        }
    }

    public Task ApplyPluginAgentHealthDecisionAsync(
        PluginAgentHealthPolicyDecision decision,
        CancellationToken cancellationToken)
    {
        if (decision.HasOutcome(PluginAgentHealthPolicyOutcome.RetryActiveStep))
        {
            return RetryActiveStepsAsync(
                decision.Event.Identity.DisplayName,
                decision.Event.ExitCode,
                decision.Event.ProcessStepIds,
                cancellationToken);
        }

        if (decision.HasOutcome(PluginAgentHealthPolicyOutcome.FailActiveStep))
        {
            _logger.LogWarning(
                "Plugin-agent health policy requested active-step failure for {PluginName}, but no fail policy is currently mapped; affected step count: {ProcessStepCount}.",
                decision.Event.Identity.DisplayName,
                decision.Event.ProcessStepIds.Count);
        }

        return Task.CompletedTask;
    }

    public Task HandlePluginAgentExitedAsync(
        PluginAgentExitNotification notification,
        CancellationToken cancellationToken)
    {
        if (notification.ShutdownRequested)
        {
            _logger.LogInformation(
                "Plugin runtime {PluginName} exited during shutdown with {InFlightJobCount} in-flight process step(s); broker redelivery will recover unfinished work.",
                notification.PluginName,
                notification.InFlightProcessStepIds.Count);
            return Task.CompletedTask;
        }

        return RetryActiveStepsAsync(
            notification.PluginName,
            notification.ExitCode,
            notification.InFlightProcessStepIds,
            cancellationToken);
    }

    private async Task RetryActiveStepsAsync(
        string pluginName,
        int? exitCode,
        IReadOnlyCollection<Guid> processStepIds,
        CancellationToken cancellationToken)
    {
        foreach (var processStepId in processStepIds.Distinct())
        {
            if (!_activeSteps.TryRemove(processStepId, out var context))
            {
                _logger.LogWarning(
                    "Plugin runtime {PluginName} reported abandoned process step {ProcessStepId}, but no active step context was found.",
                    pluginName,
                    processStepId);
                continue;
            }

            try
            {
                var resetResult = await _stepClaimService.TryResetStepToPendingAsync(
                    context,
                    cancellationToken);

                if (resetResult is StepResetResult.ResetToPending or StepResetResult.AlreadyPending)
                {
                    await context.AbandonMessageAsync(cancellationToken);
                    _pluginRuntimeAgentTelemetry.RecordActiveStepCleanupSucceeded(pluginName);
                    _logger.LogWarning(
                        "Cleaned up abandoned process step {ProcessStepId} after plugin runtime {PluginName} exited with code {ExitCode}; queue message was abandoned for redelivery.",
                        processStepId,
                        pluginName,
                        exitCode);
                    continue;
                }

                if (resetResult is StepResetResult.AlreadyTerminal)
                {
                    _pluginRuntimeAgentTelemetry.RecordActiveStepCleanupSucceeded(pluginName);
                    _logger.LogInformation(
                        "Removed abandoned active context for terminal process step {ProcessStepId} after plugin runtime {PluginName} exited with code {ExitCode}.",
                        processStepId,
                        pluginName,
                        exitCode);
                    continue;
                }

                _pluginRuntimeAgentTelemetry.RecordActiveStepCleanupFailed(pluginName);
                _logger.LogError(
                    "Failed to reset abandoned process step {ProcessStepId} after plugin runtime {PluginName} exited with code {ExitCode}; queue message was left unsettled.",
                    processStepId,
                    pluginName,
                    exitCode);
            }
            catch (Exception ex)
            {
                _pluginRuntimeAgentTelemetry.RecordActiveStepCleanupFailed(pluginName, ex);
                _logger.LogError(
                    ex,
                    "Failed to clean up abandoned process step {ProcessStepId} after plugin runtime {PluginName} exited with code {ExitCode}; queue message was left unsettled.",
                    processStepId,
                    pluginName,
                    exitCode);
            }
            finally
            {
                await context.StopLockRenewalAsync();
                _processLifecycleTelemetry.StopStepActivity(
                    context,
                    result: null,
                    exception: new InvalidOperationException(
                        $"Plugin runtime {pluginName} exited before returning a result for process step {processStepId}."));
                _agentManager.ReleaseStepSlot(context.ResolvedTaskVersionId);
            }
        }
    }

    private async Task<bool> TryResolvePluginRuntimeAsync(
        StepExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (_pluginRuntimeCatalog.TryResolvePluginRuntime(
                context.TaskDefinitionId,
                context.AllowedMajor,
                context.AllowedMinor,
                out var resolution))
        {
            context.ApplyPluginRuntime(resolution);
            return true;
        }

        await _pluginRuntimeReloader.ReloadAsync(cancellationToken);

        if (_pluginRuntimeCatalog.TryResolvePluginRuntime(
                context.TaskDefinitionId,
                context.AllowedMajor,
                context.AllowedMinor,
                out resolution))
        {
            context.ApplyPluginRuntime(resolution);
            return true;
        }

        return false;
    }
}
