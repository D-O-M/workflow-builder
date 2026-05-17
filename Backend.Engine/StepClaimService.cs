using System.Diagnostics;
using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;

namespace Backend.Engine;

public sealed record StepClaimResult(
    bool StepClaimed,
    bool ProcessStarted);

public sealed class StepClaimService
{
    private readonly IProcessStepRepository _processStepRepository;
    private readonly IProcessInstanceRepository<Dictionary<string, JsonElement>> _processInstanceRepository;
    private readonly ProcessLifecycleTelemetry _processLifecycleTelemetry;

    public StepClaimService(
        IProcessStepRepository processStepRepository,
        IProcessInstanceRepository<Dictionary<string, JsonElement>> processInstanceRepository,
        ProcessLifecycleTelemetry processLifecycleTelemetry)
    {
        _processStepRepository = processStepRepository;
        _processInstanceRepository = processInstanceRepository;
        _processLifecycleTelemetry = processLifecycleTelemetry;
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
        var step = await _processStepRepository.GetProcessStepById(
            context.ProcessStepId,
            context.ProcessId,
            cancellationToken);

        if (step.Status is not ProcessStepStatus.InProgress)
        {
            return;
        }

        step.Status = ProcessStepStatus.Pending;
        step.UpdatedAt = DateTimeOffset.UtcNow;
        step.StartedAt = null;
        await _processStepRepository.UpdateProcessStep(step, cancellationToken);
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

        if (process.Status is ProcessStatus.Pending)
        {
            process.Status = ProcessStatus.InProgress;
            process.UpdatedAt = now;
            await _processInstanceRepository.UpdateProcessState(process, cancellationToken);
            return new StepClaimResult(true, true);
        }

        return new StepClaimResult(true, false);
    }

    private Task<ProcessInstanceRecord<Dictionary<string, JsonElement>>> GetProcessStateAsync(
        StepExecutionContext context,
        CancellationToken cancellationToken) =>
        _processInstanceRepository.GetProcessStateById(
            context.ProcessId,
            context.ExecutableBlueprintId,
            cancellationToken);

    internal static void EnsureExecutionProgress(
        StepExecutionContext context,
        ProcessInstanceRecord<Dictionary<string, JsonElement>> process)
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
