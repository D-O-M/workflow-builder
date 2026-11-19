using System.Diagnostics;
using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;

namespace Backend.Engine;

public sealed record StepClaimResult(
    bool StepClaimed,
    bool ProcessStarted);

public enum StepResetResult
{
    ResetToPending,
    AlreadyPending,
    AlreadyTerminal,
    NotReset
}

public sealed class StepClaimService
{
    private readonly IProcessStepRepository _processStepRepository;
    private readonly IProcessInstanceRepository _processInstanceRepository;
    private readonly IProcessIndexProjectionService? _processIndexProjectionService;
    private readonly IProcessRealtimeProjectionService? _processRealtimeProjectionService;
    private readonly EngineProjectionOptions _projectionOptions;
    private readonly ProcessLifecycleTelemetry _processLifecycleTelemetry;
    private readonly ILogger<StepClaimService>? _logger;

    public StepClaimService(
        IProcessStepRepository processStepRepository,
        IProcessInstanceRepository processInstanceRepository,
        ProcessLifecycleTelemetry processLifecycleTelemetry,
        IProcessIndexProjectionService? processIndexProjectionService = null,
        ILogger<StepClaimService>? logger = null,
        IProcessRealtimeProjectionService? processRealtimeProjectionService = null,
        EngineProjectionOptions? projectionOptions = null)
    {
        _processStepRepository = processStepRepository;
        _processInstanceRepository = processInstanceRepository;
        _processIndexProjectionService = processIndexProjectionService;
        _processRealtimeProjectionService = processRealtimeProjectionService;
        _projectionOptions = projectionOptions ?? EngineProjectionOptions.Enabled;
        _processLifecycleTelemetry = processLifecycleTelemetry;
        _logger = logger;
    }

    public async Task<StepClaimResult> TryClaimStepAsync(
        StepExecutionContext context,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await TryClaimStepCoreAsync(context, cancellationToken);
        stopwatch.Stop();

        _processLifecycleTelemetry.RecordStepClaim(
            context,
            stopwatch.Elapsed,
            result.StepClaimed);

        return result;
    }

    public async Task ResetStepToPendingAsync(
        StepExecutionContext context,
        CancellationToken cancellationToken)
    {
        _ = await TryResetStepToPendingAsync(context, cancellationToken);
    }

    public async Task<StepResetResult> TryResetStepToPendingAsync(
        StepExecutionContext context,
        CancellationToken cancellationToken)
    {
        var step = await _processStepRepository.GetProcessStepById(
            context.ProcessStepId,
            context.ProcessId,
            cancellationToken);

        if (step.Status is ProcessStepStatus.Pending)
        {
            return StepResetResult.AlreadyPending;
        }

        if (step.Status is ProcessStepStatus.Completed or ProcessStepStatus.Failed or ProcessStepStatus.Skipped)
        {
            return StepResetResult.AlreadyTerminal;
        }

        if (step.Status is not ProcessStepStatus.InProgress)
        {
            return StepResetResult.NotReset;
        }

        var reset = await _processStepRepository.TryResetProcessStepToPendingAsync(
            context.ProcessStepId,
            context.ProcessId,
            DateTimeOffset.UtcNow,
            cancellationToken);

        return reset
            ? StepResetResult.ResetToPending
            : StepResetResult.NotReset;
    }

    public async Task HydrateStepForFinalizationAsync(
        StepExecutionContext context,
        CancellationToken cancellationToken)
    {
        var step = await GetPersistedStepAsync(context, cancellationToken);
        var process = await GetProcessStateAsync(context, cancellationToken);
        EnsureExecutionProgress(context, process);
        context.ApplyFinalizationState(step, process);
    }

    public Task<ProcessStepRecord> GetPersistedStepAsync(
        StepExecutionContext context,
        CancellationToken cancellationToken) =>
        _processStepRepository.GetProcessStepById(
            context.ProcessStepId,
            context.ProcessId,
            cancellationToken);

    private async Task<StepClaimResult> TryClaimStepCoreAsync(
        StepExecutionContext context,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var (claimed, step) = await _processStepRepository.TryClaimProcessStepAsync(
            context.ProcessStepId,
            context.ProcessId,
            context.TaskDefinitionId,
            context.AllowedMajor,
            context.AllowedMinor,
            context.ResolvedTaskVersionId,
            context.BlueprintNode.ResolvedVersion.ToString(),
            now,
            cancellationToken);

        if (!claimed || step is null)
        {
            return new StepClaimResult(false, false);
        }

        if (step.InputHydrationPlan is null)
        {
            throw new InvalidOperationException(
                $"Process step {context.ProcessStepId} has no input hydration plan.");
        }

        var process = await GetProcessStateAsync(context, cancellationToken);
        EnsureExecutionProgress(context, process);
        context.ApplyClaimedStep(step, process);

        if (process.Recovery is { } recovery &&
            (!recovery.Active || recovery.RecoveryId != step.RecoveryId || recovery.Revision != step.RecoveryRevision ||
             !recovery.EffectiveAttemptIds.TryGetValue(step.LogicalStepId ?? step.Id, out var current) || current != step.Id))
        {
            // A delivery for a superseded physical attempt can settle without invoking its Plugin.
            return new StepClaimResult(false, false);
        }

        if (process.Status is ProcessStatus.Pending)
        {
            process.Status = ProcessStatus.InProgress;
            process.UpdatedAt = now;
            await _processInstanceRepository.UpdateProcessState(process, cancellationToken);
            await TryProjectProcessAsync(context, process, cancellationToken);
            await TryProjectRealtimeAsync(context, process, step, cancellationToken);
            return new StepClaimResult(true, true);
        }

        await TryProjectRealtimeAsync(context, process, step, cancellationToken);
        return new StepClaimResult(true, false);
    }

    private async Task TryProjectProcessAsync(
        StepExecutionContext context,
        ProcessInstanceRecord process,
        CancellationToken cancellationToken)
    {
        if (!_projectionOptions.SynchronousEngineUpdatesEnabled ||
            _processIndexProjectionService is null)
        {
            return;
        }

        try
        {
            await _processIndexProjectionService.UpsertFromProcessAsync(
                process,
                context.BlueprintDefinitionId,
                context.ExecutableBlueprintId,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.LogWarning(
                exception,
                "Process index projection failed for process {ProcessId} after step claim.",
                context.ProcessId);
        }
    }

    private async Task TryProjectRealtimeAsync(
        StepExecutionContext context,
        ProcessInstanceRecord process,
        ProcessStepRecord step,
        CancellationToken cancellationToken)
    {
        if (!_projectionOptions.SynchronousEngineUpdatesEnabled ||
            _processRealtimeProjectionService is null)
        {
            return;
        }

        try
        {
            await _processRealtimeProjectionService.UpsertFromProcessAsync(
                process,
                context.BlueprintDefinitionId,
                context.ExecutableBlueprintId,
                cancellationToken);
            await _processRealtimeProjectionService.UpsertFromStepAsync(
                step,
                context.BlueprintDefinitionId,
                context.ExecutableBlueprintId,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.LogWarning(
                exception,
                "Realtime projection failed for claimed process step {ProcessStepId}.",
                context.ProcessStepId);
        }
    }

    private Task<ProcessInstanceRecord> GetProcessStateAsync(
        StepExecutionContext context,
        CancellationToken cancellationToken) =>
        _processInstanceRepository.GetProcessStateById(
            context.ProcessId,
            context.ExecutableBlueprintId,
            cancellationToken);

    internal static void EnsureExecutionProgress(
        StepExecutionContext context,
        ProcessInstanceRecord process)
    {
        process.ExecutionProgress ??= new ProcessExecutionProgress();
        if (process.ExecutionProgress.TotalNodeCount <= 0)
        {
            process.ExecutionProgress.TotalNodeCount = context.ExecutableBlueprint.AllNodes.Count;
        }

        if (process.ExecutionProgress.TotalStepCount <= 0)
        {
            process.ExecutionProgress.TotalStepCount = process.ExecutionProgress.TotalNodeCount;
        }
    }
}
