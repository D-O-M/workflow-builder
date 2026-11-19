using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Repositories;
using Microsoft.Extensions.Logging;

namespace Backend.Repositories.Processes;

public sealed class ProcessRealtimeProjectionService : IProcessRealtimeProjectionService
{
    private const int MaxUpdateAttempts = 10;

    private readonly IProcessRealtimeProjectionRepository _repository;
    private readonly ILogger<ProcessRealtimeProjectionService>? _logger;

    public ProcessRealtimeProjectionService(
        IProcessRealtimeProjectionRepository repository,
        ILogger<ProcessRealtimeProjectionService>? logger = null)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task UpsertFromProcessAsync(
        ProcessInstanceRecord process,
        int blueprintDefinitionId,
        int blueprintVersionId,
        CancellationToken cancellationToken)
    {
        var incoming = CreateSummaryProjection(process, blueprintDefinitionId, blueprintVersionId);
        await MergeAndSaveSummaryAsync(incoming, cancellationToken);
    }

    public async Task UpsertFromStepAsync(
        ProcessStepRecord step,
        int blueprintDefinitionId,
        int blueprintVersionId,
        CancellationToken cancellationToken)
    {
        await UpsertSummaryFromStepAsync(step, blueprintDefinitionId, blueprintVersionId, cancellationToken);
        await UpsertNodeFromStepAsync(step, cancellationToken);
    }

    public async Task UpsertFromProcessAndStepsAsync(
        ProcessInstanceRecord process,
        int blueprintDefinitionId,
        int blueprintVersionId,
        IReadOnlyCollection<ProcessStepRecord> steps,
        CancellationToken cancellationToken)
    {
        var summary = CreateSummaryProjection(process, blueprintDefinitionId, blueprintVersionId);
        var current = ProcessRecoveryState.EffectiveSteps(process, steps);
        foreach (var step in current)
        {
            summary.StepStatusContributions[step.LogicalStepId ?? step.Id] = CreateSummaryContribution(step);
        }

        await MergeAndSaveSummaryAsync(summary, cancellationToken);

        foreach (var group in current
            .Where(step => Guid.TryParse(step.BlueprintNodeId, out _))
            .GroupBy(step => step.BlueprintNodeId, StringComparer.Ordinal))
        {
            var node = CreateNodeProjection(group.First());
            foreach (var step in group)
            {
                node.StepContributions[step.LogicalStepId ?? step.Id] = CreateNodeContribution(step);
            }

            RecalculateNode(node);
            await UpsertRecalculatedNodeAsync(node, cancellationToken);
        }
    }

    public Task<ProcessRealtimeSummaryProjectionRecord?> GetSummaryAsync(
        Guid processId,
        CancellationToken cancellationToken) =>
        _repository.GetSummaryAsync(processId, cancellationToken);

    public Task<IReadOnlyList<ProcessNodeRealtimeProjectionRecord>> GetNodeProjectionsAsync(
        Guid processId,
        IReadOnlyCollection<Guid> blueprintNodeIds,
        CancellationToken cancellationToken) =>
        _repository.GetNodesAsync(processId, blueprintNodeIds, cancellationToken);

    private async Task UpsertSummaryFromStepAsync(
        ProcessStepRecord step,
        int blueprintDefinitionId,
        int blueprintVersionId,
        CancellationToken cancellationToken)
    {
        var summary = new ProcessRealtimeSummaryProjectionRecord
        {
            id = step.ProcessId,
            ProcessId = step.ProcessId,
            BlueprintDefinitionId = blueprintDefinitionId,
            BlueprintVersionId = blueprintVersionId,
            Status = step.Status is ProcessStepStatus.Failed
                ? ProcessStatus.Failed
                : ProcessStatus.Pending,
            CreatedAt = step.CreatedAt,
            UpdatedAt = step.UpdatedAt ?? step.CreatedAt,
            IndexedAt = DateTimeOffset.UtcNow
        };

        summary.StepStatusContributions[step.LogicalStepId ?? step.Id] = CreateSummaryContribution(step);
        await MergeAndSaveSummaryAsync(summary, cancellationToken);
    }

    private async Task MergeAndSaveSummaryAsync(ProcessRealtimeSummaryProjectionRecord incoming, CancellationToken cancellationToken)
    {
        var processId = Guid.Parse(incoming.ProcessId);
        for (var attempt = 0; attempt < MaxUpdateAttempts; attempt++)
        {
            var existing = await _repository.GetSummaryAsync(processId, cancellationToken);
            var summary = MergeSummary(existing, incoming);
            if (await _repository.TrySaveSummaryAsync(summary, create: existing is null, cancellationToken))
                return;
        }
        throw new InvalidOperationException($"Could not update realtime summary for Process {processId}.");
    }

    private async Task UpsertNodeFromStepAsync(
        ProcessStepRecord step,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(step.ProcessId, out var processId) ||
            !Guid.TryParse(step.BlueprintNodeId, out var blueprintNodeId))
        {
            return;
        }

        for (var attempt = 0; attempt < MaxUpdateAttempts; attempt++)
        {
            var existing = await _repository.GetNodeAsync(
                processId,
                blueprintNodeId,
                cancellationToken);

            var node = existing ?? CreateNodeProjection(step);
            MergeNodeContribution(node, step.LogicalStepId ?? step.Id, CreateNodeContribution(step));
            RecalculateNode(node);

            if (existing is null)
            {
                var (created, _) = await _repository.CreateNodeIfNotExistsAsync(node, cancellationToken);
                if (created)
                {
                    return;
                }

                continue;
            }

            if (await _repository.TryUpdateNodeAsync(node, cancellationToken))
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"Could not update realtime node projection for process {step.ProcessId} node {step.BlueprintNodeId}.");
    }

    private async Task UpsertRecalculatedNodeAsync(
        ProcessNodeRealtimeProjectionRecord incoming,
        CancellationToken cancellationToken)
    {
        var processId = Guid.Parse(incoming.ProcessId);
        var blueprintNodeId = Guid.Parse(incoming.BlueprintNodeId);

        for (var attempt = 0; attempt < MaxUpdateAttempts; attempt++)
        {
            var existing = await _repository.GetNodeAsync(processId, blueprintNodeId, cancellationToken);
            var node = existing is null ? incoming : MergeNode(existing, incoming);
            RecalculateNode(node);

            if (existing is null)
            {
                var (created, _) = await _repository.CreateNodeIfNotExistsAsync(node, cancellationToken);
                if (created)
                {
                    return;
                }

                continue;
            }

            if (await _repository.TryUpdateNodeAsync(node, cancellationToken))
            {
                return;
            }
        }

        _logger?.LogWarning(
            "Could not repair realtime node projection for process {ProcessId} node {BlueprintNodeId}.",
            incoming.ProcessId,
            incoming.BlueprintNodeId);
    }

    private static ProcessRealtimeSummaryProjectionRecord CreateSummaryProjection(
        ProcessInstanceRecord process,
        int blueprintDefinitionId,
        int blueprintVersionId) =>
        new()
        {
            id = process.ProcessId,
            ProcessId = process.ProcessId,
            BlueprintDefinitionId = blueprintDefinitionId,
            BlueprintVersionId = blueprintVersionId,
            Status = process.Status,
            RecoverySequence = process.Recovery?.Sequence ?? 0,
            ProcessProgressOrder = ProcessProgressOrder(process),
            TotalStepCount = Math.Max(
                process.ExecutionProgress?.TotalStepCount ?? 0,
                (process.ExecutionProgress?.CompletedStepIds.Count ?? 0) +
                    (process.ExecutionProgress?.FailedStepIds.Count ?? 0) + (process.ExecutionProgress?.SkippedStepIds.Count ?? 0)),
            SkippedStepCount = process.ExecutionProgress?.SkippedStepIds.Count ?? 0,
            CompletedStepCount = process.ExecutionProgress?.CompletedStepIds.Count ?? 0,
            FailedStepCount = process.ExecutionProgress?.FailedStepIds.Count ?? 0,
            PendingStepCount = process.Status is ProcessStatus.Pending ? 1 : 0,
            InProgressStepCount = process.Status is ProcessStatus.InProgress ? 1 : 0,
            CreatedAt = process.CreatedAt,
            UpdatedAt = process.UpdatedAt ?? process.CreatedAt,
            IndexedAt = DateTimeOffset.UtcNow
        };

    private static long ProcessProgressOrder(ProcessInstanceRecord process)
    {
        // A recovery can reopen completed work. Its canonical writes are sequenced by its coordinator.
        if (process.Recovery is not null) return (process.UpdatedAt ?? process.CreatedAt).UtcTicks;
        // Cosmos ETags are opaque and finalization can replay an older completion time.
        // These sets only grow and each iterator adjustment has a distinct expansion ID.
        var progress = process.ExecutionProgress;
        long effects = (progress?.CompletedStepIds.Count ?? 0) + (long)(progress?.FailedStepIds.Count ?? 0)
            + (progress?.SkippedStepIds.Count ?? 0) + (progress?.ExpandedCollectionIteratorStepIds.Count ?? 0);
        return effects * 4 + (int)process.Status;
    }

    private static ProcessNodeRealtimeProjectionRecord CreateNodeProjection(ProcessStepRecord step) =>
        new()
        {
            id = ProcessRealtimeProjectionRepository.CreateNodeId(Guid.Parse(step.BlueprintNodeId)),
            ProcessId = step.ProcessId,
            BlueprintNodeId = step.BlueprintNodeId,
            AggregateStatus = step.Status,
            RuntimeStepCount = 0,
            CreatedAt = step.CreatedAt,
            UpdatedAt = step.UpdatedAt ?? step.CreatedAt,
            IndexedAt = DateTimeOffset.UtcNow
        };

    private static ProcessRealtimeStepStatusContribution CreateSummaryContribution(ProcessStepRecord step) =>
        new()
        {
            AttemptOrder = AttemptOrder(step),
            ProcessStepId = step.Id,
            Status = step.Status,
            CreatedAt = step.CreatedAt,
            UpdatedAt = step.UpdatedAt ?? step.CreatedAt
        };

    private static ProcessNodeRealtimeStepContribution CreateNodeContribution(ProcessStepRecord step) =>
        new()
        {
            AttemptOrder = AttemptOrder(step),
            ProcessStepId = step.Id,
            Status = step.Status,
            SelectedChannel = step.OutputData?.Count == 1 && step.OutputData.Keys.First() is "True" or "False" ? step.OutputData.Keys.First() : null,
            IterationContext = step.IterationContext,
            ResolvedTaskVersionId = step.ResolvedTaskVersionId,
            ResolvedTaskVersion = step.ResolvedTaskVersion,
            StartedAt = step.StartedAt,
            CompletedAt = step.CompletedAt,
            FailedAt = step.FailedAt,
            ErrorMessage = step.ErrorMessage,
            CreatedAt = step.CreatedAt,
            UpdatedAt = step.UpdatedAt ?? step.CreatedAt
        };

    private static ProcessRealtimeSummaryProjectionRecord MergeSummary(
        ProcessRealtimeSummaryProjectionRecord? existing,
        ProcessRealtimeSummaryProjectionRecord incoming)
    {
        if (existing is null)
        {
            RecalculateSummary(incoming);
            return incoming;
        }

        foreach (var (stepId, contribution) in incoming.StepStatusContributions)
        {
            if (!existing.StepStatusContributions.TryGetValue(stepId, out var previous)
                || AcceptContribution(previous.AttemptOrder, previous.Status, previous.UpdatedAt ?? previous.CreatedAt,
                    contribution.AttemptOrder, contribution.Status, contribution.UpdatedAt ?? contribution.CreatedAt))
                existing.StepStatusContributions[stepId] = contribution;
        }

        existing.BlueprintDefinitionId = incoming.BlueprintDefinitionId;
        existing.BlueprintVersionId = incoming.BlueprintVersionId;
        existing.CreatedAt = Min(existing.CreatedAt, incoming.CreatedAt);
        existing.UpdatedAt = Max(existing.UpdatedAt, incoming.UpdatedAt);
        existing.IndexedAt = DateTimeOffset.UtcNow;
        if (incoming.ProcessProgressOrder is long order &&
            (incoming.RecoverySequence > existing.RecoverySequence ||
             incoming.RecoverySequence == existing.RecoverySequence && order >= (existing.ProcessProgressOrder ?? -1)))
        {
            existing.RecoverySequence = incoming.RecoverySequence;
            existing.ProcessProgressOrder = order;
            existing.Status = incoming.Status;
            existing.TotalStepCount = incoming.TotalStepCount;
            if (existing.StepStatusContributions.Count == 0)
            {
                existing.PendingStepCount = incoming.PendingStepCount;
                existing.InProgressStepCount = incoming.InProgressStepCount;
            }
            existing.SkippedStepCount = incoming.SkippedStepCount;
            existing.CompletedStepCount = incoming.CompletedStepCount;
            existing.FailedStepCount = incoming.FailedStepCount;
        }

        RecalculateSummary(existing);
        return existing;
    }

    private static ProcessNodeRealtimeProjectionRecord MergeNode(
        ProcessNodeRealtimeProjectionRecord existing,
        ProcessNodeRealtimeProjectionRecord incoming)
    {
        foreach (var (stepId, contribution) in incoming.StepContributions)
        {
            MergeNodeContribution(existing, stepId, contribution);
        }

        existing.CreatedAt = Min(existing.CreatedAt, incoming.CreatedAt);
        existing.UpdatedAt = Max(existing.UpdatedAt, incoming.UpdatedAt);
        existing.IndexedAt = DateTimeOffset.UtcNow;
        return existing;
    }

    private static void MergeNodeContribution(ProcessNodeRealtimeProjectionRecord node, string stepId, ProcessNodeRealtimeStepContribution contribution)
    {
        if (!node.StepContributions.TryGetValue(stepId, out var previous)
            || AcceptContribution(previous.AttemptOrder, previous.Status, previous.UpdatedAt ?? previous.CreatedAt,
                contribution.AttemptOrder, contribution.Status, contribution.UpdatedAt ?? contribution.CreatedAt))
            node.StepContributions[stepId] = contribution;
    }

    // A missing phase handoff can put a completed Step back into review within the same recovery.
    private static long AttemptOrder(ProcessStepRecord step) =>
        step.RecoverySequence * 2 + (step.RecoveryAction == "Review" ? 1 : 0);

    private static bool AcceptContribution(long previousSequence, ProcessStepStatus previous, DateTimeOffset previousTime,
        long incomingSequence, ProcessStepStatus incoming, DateTimeOffset incomingTime)
    {
        if (previousSequence != incomingSequence) return incomingSequence > previousSequence;
        if (previous is ProcessStepStatus.Completed or ProcessStepStatus.Failed or ProcessStepStatus.Skipped)
            return previous == incoming && incomingTime >= previousTime;
        if (incoming is ProcessStepStatus.Completed or ProcessStepStatus.Failed or ProcessStepStatus.Skipped)
            return true;
        return incomingTime > previousTime || (incomingTime == previousTime && incoming >= previous);
    }

    private static void RecalculateSummary(ProcessRealtimeSummaryProjectionRecord summary)
    {
        if (summary.StepStatusContributions.Count > 0)
        {
            var contributions = summary.StepStatusContributions.Values.ToList();
            summary.TotalStepCount = Math.Max(summary.TotalStepCount, contributions.Count);
            summary.PendingStepCount = contributions.Count(contribution => contribution.Status is ProcessStepStatus.Pending);
            summary.InProgressStepCount = contributions.Count(contribution => contribution.Status is ProcessStepStatus.InProgress);
            var completed = contributions.Count(c => c.Status == ProcessStepStatus.Completed);
            var skipped = contributions.Count(c => c.Status == ProcessStepStatus.Skipped);
            var failed = contributions.Count(c => c.Status == ProcessStepStatus.Failed);
            summary.CompletedStepCount = summary.RecoverySequence > 0 ? completed : Math.Max(summary.CompletedStepCount, completed);
            summary.SkippedStepCount = summary.RecoverySequence > 0 ? skipped : Math.Max(summary.SkippedStepCount, skipped);
            summary.FailedStepCount = summary.RecoverySequence > 0 ? failed : Math.Max(summary.FailedStepCount, failed);
            if (summary.ProcessProgressOrder is null)
            {
                summary.Status = contributions.All(step => step.Status == ProcessStepStatus.Pending)
                    ? ProcessStatus.Pending : ProcessStatus.InProgress;
            }
            if (summary.FailedStepCount > 0 && summary.RecoverySequence == 0)
                summary.Status = ProcessStatus.Failed;
            if (summary.Status is ProcessStatus.Completed)
            {
                summary.InProgressStepCount = 0;
                summary.PendingStepCount = 0;
            }
            summary.UpdatedAt = Max(summary.UpdatedAt, contributions.Max(step => step.UpdatedAt ?? step.CreatedAt));
        }

        summary.IndexedAt = DateTimeOffset.UtcNow;
    }

    private static void RecalculateNode(ProcessNodeRealtimeProjectionRecord node)
    {
        var contributions = node.StepContributions.Values.ToList();
        if (contributions.Count == 0)
        {
            return;
        }

        var latest = contributions
            .OrderByDescending(contribution => contribution.UpdatedAt ?? contribution.CreatedAt)
            .ThenByDescending(contribution => contribution.CreatedAt)
            .First();
        var failure = contributions
            .Where(contribution => contribution.Status is ProcessStepStatus.Failed)
            .OrderByDescending(contribution => contribution.FailedAt ?? contribution.UpdatedAt ?? contribution.CreatedAt)
            .FirstOrDefault();

        node.AggregateStatus = MapStepStatus(contributions);
        node.RuntimeStepCount = contributions.Count;
        node.LatestProcessStepId = latest.ProcessStepId;
        node.ResolvedTaskVersionId = latest.ResolvedTaskVersionId;
        node.ResolvedTaskVersion = latest.ResolvedTaskVersion;
        node.StartedAt = contributions
            .Where(contribution => contribution.StartedAt.HasValue)
            .Select(contribution => contribution.StartedAt)
            .Min();
        node.CompletedAt = contributions.All(contribution => contribution.Status is ProcessStepStatus.Completed or ProcessStepStatus.Skipped)
            ? contributions
                .Where(contribution => contribution.CompletedAt.HasValue)
                .Select(contribution => contribution.CompletedAt)
                .Max()
            : null;
        node.FailedAt = contributions
            .Where(contribution => contribution.FailedAt.HasValue)
            .Select(contribution => contribution.FailedAt)
            .Max();
        node.FailureSummary = failure is null
            ? null
            : new ProcessNodeRealtimeFailureSummary
            {
                ProcessStepId = failure.ProcessStepId,
                Message = failure.ErrorMessage,
                FailedAt = failure.FailedAt
            };
        node.CreatedAt = contributions.Min(contribution => contribution.CreatedAt);
        node.UpdatedAt = contributions
            .Select(contribution => contribution.UpdatedAt ?? contribution.CreatedAt)
            .Max();
        node.IndexedAt = DateTimeOffset.UtcNow;
    }

    private static ProcessStepStatus MapStepStatus(IReadOnlyCollection<ProcessNodeRealtimeStepContribution> contributions)
    {
        if (contributions.Any(contribution => contribution.Status is ProcessStepStatus.Failed))
        {
            return ProcessStepStatus.Failed;
        }

        if (contributions.Any(contribution => contribution.Status is ProcessStepStatus.InProgress))
        {
            return ProcessStepStatus.InProgress;
        }

        if (contributions.Any(contribution => contribution.Status is ProcessStepStatus.Pending))
        {
            return contributions.All(contribution => contribution.Status is ProcessStepStatus.Pending)
                ? ProcessStepStatus.Pending
                : ProcessStepStatus.InProgress;
        }

        if (contributions.All(c => c.Status == ProcessStepStatus.Skipped)) return ProcessStepStatus.Skipped;
        return contributions.All(contribution => contribution.Status is ProcessStepStatus.Completed or ProcessStepStatus.Skipped)
            ? ProcessStepStatus.Completed
            : ProcessStepStatus.Pending;
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right) =>
        left.HasValue && right.HasValue
            ? left.Value >= right.Value ? left : right
            : left ?? right;
}
