using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Models;
using Backend.Repositories;
using Backend.Services.ProcessStepQueue;

namespace Backend.Dispatcher;

public sealed record RecoveryExecutionResult(string State, string? Detail = null);

public sealed class ProcessRecoveryExecutor(
    IProcessInstanceRepository processes, IProcessStepRepository steps,
    IProcessStepFinalizationRepository checkpoints, IBlueprintExecutionCache blueprints,
    IProcessStepDispatcher dispatcher, PhasedProcessFinalizationService phases,
    IProcessIndexProjectionService indexes, IProcessRealtimeProjectionService realtime)
{
    private const int MaxMaterializationsPerPass = 32;

    public async Task<RecoveryExecutionResult> AdvanceAsync(
        Guid recoveryId, int revision, RecoveryCasePlan plan, CancellationToken cancellationToken)
    {
        var process = await processes.GetProcessStateById(plan.ProcessId, plan.BlueprintVersionId, cancellationToken);
        var state = process.Recovery;
        var owns = state?.RecoveryId == recoveryId && state.Revision == revision;
        var blueprint = await blueprints.GetExecutableBlueprintAsync(plan.BlueprintVersionId, cancellationToken);
        if (owns && !state!.Active)
        {
            await ProjectProcessAsync(process, blueprint, cancellationToken);
            return new(state.Outcome, state.Detail);
        }
        var history = await steps.GetProcessStepsForProcess(plan.ProcessId, cancellationToken);
        if (!owns)
        {
            if (ProcessRecoveryGraph.AmbiguousOccurrence(blueprint, plan) is { } ambiguity)
                return new("Changed", ambiguity);
            var effective = ProcessRecoveryState.EffectiveSteps(process, history);
            if (state?.Active == true || effective.Any(IsActive))
                return new("Changed", "The Process still has active work.");
            var targetLogicalIds = plan.Steps.Where(row => plan.TargetStepIds.Contains(row.StepId))
                .Select(row => row.LogicalStepId.ToString()).ToHashSet();
            if (targetLogicalIds.Count > 0 && !effective.Any(step =>
                    targetLogicalIds.Contains(step.LogicalStepId ?? step.Id) && step.Status == ProcessStepStatus.Failed))
                return new("AlreadyRecovered", "The selected failures have already been resolved.");
            if (plan.ProcessETag != process.ETag || plan.TargetStepIds.Count == 0 || plan.TargetStepIds.Any(id =>
                    !effective.Any(step => step.Id == id.ToString() && step.Status == ProcessStepStatus.Failed)))
                return new("Changed", "The selected failed Steps changed after preview.");
            // A terminal Step can precede its join/phase effects. Let the old finalizer finish first.
            if (!await CheckpointsSettledAsync(effective, cancellationToken))
                return new("Changed", "A previous attempt is still finalizing.");
            process.Recovery = new ProcessRecoveryState
            {
                RecoveryId = recoveryId, Revision = revision, Sequence = (state?.Sequence ?? 0) + 1,
                EffectiveAttemptIds = state?.EffectiveAttemptIds ?? new(StringComparer.Ordinal)
            };
            if (effective.All(step => step.Status != ProcessStepStatus.Failed || plan.TargetStepIds.Contains(Guid.Parse(step.Id))))
                process.Status = ProcessStatus.InProgress;
            return await SaveProgressAsync(process, blueprint, cancellationToken);
        }

        if (state!.Outcome == "Finalizing")
            return await FinalizePhaseAsync(process, blueprint, cancellationToken);

        var graph = new ProcessRecoveryGraph(recoveryId, revision, plan, blueprint, process, history);
        graph.Evaluate();
        foreach (var pending in graph.Running.Where(s => s.IsDispatchable && s.Dispatch?.Status != ProcessStepDispatchStatus.Dispatched)
                     .Take(MaxMaterializationsPerPass))
            await EnsureDispatchAsync(pending, cancellationToken);

        if (graph.Ready.Count > 0)
        {
            var ready = graph.Ready.Take(MaxMaterializationsPerPass).ToList();
            // Reserve exact identities before creation. A crash after this CAS is repaired by this same pass.
            foreach (var step in ready) state.EffectiveAttemptIds[step.LogicalStepId!] = step.Id;
            var prospective = ProcessRecoveryState.EffectiveSteps(process, history.Concat(ready));
            RecountActive(process, blueprint, plan, prospective);
            process.UpdatedAt = DateTimeOffset.UtcNow;
            if (!await processes.TryUpdateProcessState(process, cancellationToken)) return new("Running");
            foreach (var (_, materialized) in await steps.CreateProcessStepsIfNotExists(ready, cancellationToken))
            {
                if (materialized.IsDispatchable) await EnsureDispatchAsync(materialized, cancellationToken);
                await realtime.UpsertFromStepAsync(materialized, blueprint.DefinitionId, blueprint.Id, cancellationToken);
            }
            await ProjectProcessAsync(process, blueprint, cancellationToken);
            return new("Running");
        }
        var current = ProcessRecoveryState.EffectiveSteps(process, history);
        if (graph.Running.Count > 0)
        {
            var progress = process.ExecutionProgress;
            if (!progress.CompletedStepIds.SetEquals(current.Where(s => s.Status == ProcessStepStatus.Completed).Select(s => s.Id)) ||
                !progress.FailedStepIds.SetEquals(current.Where(s => s.Status == ProcessStepStatus.Failed).Select(s => s.Id)))
            {
                RecountActive(process, blueprint, plan, current);
                return await SaveProgressAsync(process, blueprint, cancellationToken);
            }
            return new("Running");
        }
        if (!await CheckpointsSettledAsync(current.Where(s => s.RecoveryId == recoveryId && s.RecoveryRevision == revision), cancellationToken))
            return new("Running");
        Recount(process, blueprint, current);
        state.Outcome = graph.Held.Count > 0 ? "Held" : graph.Failed.Count > 0 ? "Failed" : "Completed";
        state.Detail = (graph.Held.FirstOrDefault() ?? graph.Failed.FirstOrDefault())?.ErrorMessage;
        if (state.Outcome == "Completed" && current.Any(step => step.Status == ProcessStepStatus.Failed &&
                plan.TargetStepIds.Contains(Guid.Parse(step.Id))))
        {
            state.Outcome = "Held";
            state.Detail = "A selected failure is waiting for another failed source Step.";
        }
        if (state.Outcome == "Completed" && process.Status == ProcessStatus.Failed)
            state.Detail = "Other failed Steps remain in this Process.";
        if (process.Phase is not null && process.Status == ProcessStatus.Completed && state.Outcome == "Completed")
        {
            state.Outcome = "Finalizing";
            process.Status = ProcessStatus.InProgress;
        }
        return await SaveProgressAsync(process, blueprint, cancellationToken);
    }

    private async Task<bool> CheckpointsSettledAsync(IEnumerable<ProcessStepRecord> records, CancellationToken cancellationToken)
    {
        foreach (var step in records.Where(s => s.RecoveryAction is null or "Run").Where(s =>
                     s.Status is ProcessStepStatus.Completed or ProcessStepStatus.Failed))
        {
            var checkpoint = await checkpoints.GetAsync(Guid.Parse(step.Id), cancellationToken);
            if (checkpoint is not null && checkpoint.Stage is not
                (ProcessStepFinalizationStage.QueueMessageCompletionPrepared or ProcessStepFinalizationStage.RecoveredWithoutQueueMessage))
                return false;
        }
        return true;
    }

    private async Task EnsureDispatchAsync(ProcessStepRecord pending, CancellationToken cancellationToken)
    {
        var intent = await dispatcher.EnsureDispatchIntentAsync(pending, pending.CreatedAt, cancellationToken);
        await dispatcher.TryDispatchAsync(pending, intent, cancellationToken);
    }

    private async Task<RecoveryExecutionResult> FinalizePhaseAsync(
        ProcessInstanceRecord process, ExecutableBlueprint blueprint, CancellationToken cancellationToken)
    {
        try
        {
            await phases.FinalizeCompletedPhaseAsync(process.Phase!, blueprint.Id, Guid.Parse(process.Id),
                process.UpdatedAt ?? DateTimeOffset.UtcNow, cancellationToken, recoveredProcess: process);
            process.Recovery!.Outcome = "Completed";
            process.Status = ProcessStatus.Completed;
        }
        catch (MissingRecoveredHandoffException ex)
        {
            process.Status = ProcessStatus.Failed;
            process.Recovery!.Outcome = "Held";
            process.Recovery.Detail = ex.Message;
            await RecordMissingHandoffAsync(process, blueprint, ex, cancellationToken);
        }
        return await SaveProgressAsync(process, blueprint, cancellationToken);
    }

    private async Task RecordMissingHandoffAsync(ProcessInstanceRecord process, ExecutableBlueprint blueprint,
        MissingRecoveredHandoffException error, CancellationToken cancellationToken)
    {
        var history = await steps.GetProcessStepsForProcess(Guid.Parse(process.Id), cancellationToken);
        var effective = ProcessRecoveryState.EffectiveSteps(process, history);
        var nodeId = error.SourceNodeId is { } sourceId && blueprint.NodesById.ContainsKey(sourceId)
            ? sourceId : blueprint.InitialNode.Id;
        var source = effective.Where(s => s.BlueprintNodeId == nodeId.ToString()).MaxBy(s => s.UpdatedAt ?? s.CreatedAt);
        var logicalId = source?.LogicalStepId ?? source?.Id ?? ProcessStepIdentity.Create(Guid.Parse(process.Id), nodeId).ToString();
        var state = process.Recovery!;
        var now = DateTimeOffset.UtcNow;
        var marker = new ProcessStepRecord
        {
            Id = ProcessStepIdentity.Create(state.RecoveryId, Guid.Parse(logicalId), $"handoff:{state.Revision}").ToString(),
            LogicalStepId = logicalId, RecoverySourceStepId = source?.Id, RecoveryId = state.RecoveryId,
            RecoveryRevision = state.Revision, RecoverySequence = state.Sequence, RecoveryAction = "Review",
            ProcessId = process.Id, BlueprintId = process.BlueprintId, BlueprintNodeId = nodeId.ToString(),
            Status = ProcessStepStatus.Failed, ErrorMessage = error.Message,
            IterationContext = source?.IterationContext, InputHydrationPlan = source?.InputHydrationPlan,
            InputData = source?.InputData, OutputData = source?.OutputData,
            CreatedAt = now, UpdatedAt = now, FailedAt = now
        };
        var (_, saved) = await steps.CreateProcessStepIfNotExists(marker, cancellationToken);
        state.EffectiveAttemptIds[logicalId] = saved.Id;
        Recount(process, blueprint, ProcessRecoveryState.EffectiveSteps(process, history.Where(s => s.Id != saved.Id).Append(saved)));
        await realtime.UpsertFromStepAsync(saved, blueprint.DefinitionId, blueprint.Id, cancellationToken);
    }

    private async Task<RecoveryExecutionResult> SaveProgressAsync(
        ProcessInstanceRecord process, ExecutableBlueprint blueprint, CancellationToken cancellationToken)
    {
        process.UpdatedAt = DateTimeOffset.UtcNow;
        if (!await processes.TryUpdateProcessState(process, cancellationToken)) return new("Running");
        await ProjectProcessAsync(process, blueprint, cancellationToken);
        var recovery = process.Recovery!;
        return new(recovery.Active ? "Running" : recovery.Outcome, recovery.Detail);
    }

    private async Task ProjectProcessAsync(ProcessInstanceRecord process, ExecutableBlueprint blueprint, CancellationToken cancellationToken)
    {
        await indexes.UpsertFromProcessAsync(process, blueprint.DefinitionId, blueprint.Id, cancellationToken);
        await realtime.UpsertFromProcessAsync(process, blueprint.DefinitionId, blueprint.Id, cancellationToken);
    }

    private static bool IsActive(ProcessStepRecord step) => step.Status is ProcessStepStatus.Pending or ProcessStepStatus.InProgress;

    private static void RecountActive(ProcessInstanceRecord process, ExecutableBlueprint blueprint, RecoveryCasePlan plan,
        IReadOnlyList<ProcessStepRecord> effective)
    {
        Recount(process, blueprint, effective);
        var targets = plan.TargetStepIds.ToHashSet();
        process.Status = effective.Any(s => s.Status == ProcessStepStatus.Failed && !targets.Contains(Guid.Parse(s.Id)))
            ? ProcessStatus.Failed : ProcessStatus.InProgress;
    }

    private static void Recount(ProcessInstanceRecord process, ExecutableBlueprint blueprint, IReadOnlyList<ProcessStepRecord> steps)
    {
        var progress = process.ExecutionProgress;
        progress.TotalNodeCount = blueprint.AllNodes.Count;
        progress.TotalStepCount = steps.Count;
        progress.CompletedStepIds = steps.Where(s => s.Status == ProcessStepStatus.Completed).Select(s => s.Id).ToHashSet();
        progress.CompletedNodeIds = steps.Where(s => s.Status == ProcessStepStatus.Completed).Select(s => s.BlueprintNodeId).ToHashSet();
        progress.SkippedStepIds = steps.Where(s => s.Status == ProcessStepStatus.Skipped).Select(s => s.Id).ToHashSet();
        progress.FailedStepIds = steps.Where(s => s.Status == ProcessStepStatus.Failed).Select(s => s.Id).ToHashSet();
        progress.FailedNodeIds = steps.Where(s => s.Status == ProcessStepStatus.Failed).Select(s => s.BlueprintNodeId).ToHashSet();
        process.Status = progress.FailedStepIds.Count > 0 ? ProcessStatus.Failed
            : steps.Any(IsActive) ? ProcessStatus.InProgress : ProcessStatus.Completed;
    }
}
