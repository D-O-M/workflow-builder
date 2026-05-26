using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Repositories;
using Microsoft.Extensions.Logging;

namespace Backend.Repositories.Processes;

public sealed class ProcessRealtimeProjectionService : IProcessRealtimeProjectionService
{
    private const int MaxNodeUpdateAttempts = 10;

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
        var existing = await _repository.GetSummaryAsync(
            Guid.Parse(process.ProcessId),
            cancellationToken);

        await _repository.UpsertSummaryAsync(
            MergeSummary(existing, incoming),
            cancellationToken);
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
        foreach (var step in steps)
        {
            summary.StepStatusContributions[step.Id] = CreateSummaryContribution(step);
        }

        RecalculateSummary(summary);
        await _repository.UpsertSummaryAsync(summary, cancellationToken);

        foreach (var group in steps
            .Where(step => Guid.TryParse(step.BlueprintNodeId, out _))
            .GroupBy(step => step.BlueprintNodeId, StringComparer.Ordinal))
        {
            var node = CreateNodeProjection(group.First());
            foreach (var step in group)
            {
                node.StepContributions[step.Id] = CreateNodeContribution(step);
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
        var processId = Guid.Parse(step.ProcessId);
        var existing = await _repository.GetSummaryAsync(processId, cancellationToken);
        var summary = existing ?? new ProcessRealtimeSummaryProjectionRecord
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

        summary.BlueprintDefinitionId = blueprintDefinitionId;
        summary.BlueprintVersionId = blueprintVersionId;
        summary.CreatedAt = Min(summary.CreatedAt, step.CreatedAt);
        summary.StepStatusContributions[step.Id] = CreateSummaryContribution(step);
        RecalculateSummary(summary);

        await _repository.UpsertSummaryAsync(summary, cancellationToken);
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

        for (var attempt = 0; attempt < MaxNodeUpdateAttempts; attempt++)
        {
            var existing = await _repository.GetNodeAsync(
                processId,
                blueprintNodeId,
                cancellationToken);

            var node = existing ?? CreateNodeProjection(step);
            node.StepContributions[step.Id] = CreateNodeContribution(step);
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

        for (var attempt = 0; attempt < MaxNodeUpdateAttempts; attempt++)
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
            TotalStepCount = Math.Max(
                process.ExecutionProgress?.TotalStepCount ?? 0,
                (process.ExecutionProgress?.CompletedStepIds.Count ?? 0) +
                    (process.ExecutionProgress?.FailedStepIds.Count ?? 0)),
            CompletedStepCount = process.ExecutionProgress?.CompletedStepIds.Count ?? 0,
            FailedStepCount = process.ExecutionProgress?.FailedStepIds.Count ?? 0,
            PendingStepCount = process.Status is ProcessStatus.Pending ? 1 : 0,
            InProgressStepCount = process.Status is ProcessStatus.InProgress ? 1 : 0,
            CreatedAt = process.CreatedAt,
            UpdatedAt = process.UpdatedAt ?? process.CreatedAt,
            IndexedAt = DateTimeOffset.UtcNow
        };

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
            ProcessStepId = step.Id,
            Status = step.Status,
            CreatedAt = step.CreatedAt,
            UpdatedAt = step.UpdatedAt ?? step.CreatedAt
        };

    private static ProcessNodeRealtimeStepContribution CreateNodeContribution(ProcessStepRecord step) =>
        new()
        {
            ProcessStepId = step.Id,
            Status = step.Status,
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
            existing.StepStatusContributions[stepId] = contribution;
        }

        existing.BlueprintDefinitionId = incoming.BlueprintDefinitionId;
        existing.BlueprintVersionId = incoming.BlueprintVersionId;
        existing.CreatedAt = Min(existing.CreatedAt, incoming.CreatedAt);
        existing.UpdatedAt = Max(existing.UpdatedAt, incoming.UpdatedAt);
        existing.IndexedAt = DateTimeOffset.UtcNow;
        if (existing.StepStatusContributions.Count == 0)
        {
            existing.Status = incoming.Status;
            existing.TotalStepCount = Math.Max(existing.TotalStepCount, incoming.TotalStepCount);
            existing.CompletedStepCount = Math.Max(existing.CompletedStepCount, incoming.CompletedStepCount);
            existing.FailedStepCount = Math.Max(existing.FailedStepCount, incoming.FailedStepCount);
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
            existing.StepContributions[stepId] = contribution;
        }

        existing.CreatedAt = Min(existing.CreatedAt, incoming.CreatedAt);
        existing.UpdatedAt = Max(existing.UpdatedAt, incoming.UpdatedAt);
        existing.IndexedAt = DateTimeOffset.UtcNow;
        return existing;
    }

    private static void RecalculateSummary(ProcessRealtimeSummaryProjectionRecord summary)
    {
        if (summary.StepStatusContributions.Count > 0)
        {
            var contributions = summary.StepStatusContributions.Values.ToList();
            summary.TotalStepCount = contributions.Count;
            summary.PendingStepCount = contributions.Count(contribution => contribution.Status is ProcessStepStatus.Pending);
            summary.InProgressStepCount = contributions.Count(contribution => contribution.Status is ProcessStepStatus.InProgress);
            summary.CompletedStepCount = contributions.Count(contribution => contribution.Status is ProcessStepStatus.Completed);
            summary.FailedStepCount = contributions.Count(contribution => contribution.Status is ProcessStepStatus.Failed);
            summary.Status = MapProcessStatus(contributions);
            summary.UpdatedAt = contributions
                .Select(contribution => contribution.UpdatedAt ?? contribution.CreatedAt)
                .DefaultIfEmpty(summary.UpdatedAt ?? summary.CreatedAt)
                .Max();
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
        node.CompletedAt = contributions.All(contribution => contribution.Status is ProcessStepStatus.Completed)
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

    private static ProcessStatus MapProcessStatus(IReadOnlyCollection<ProcessRealtimeStepStatusContribution> contributions)
    {
        if (contributions.Any(contribution => contribution.Status is ProcessStepStatus.Failed))
        {
            return ProcessStatus.Failed;
        }

        if (contributions.Any(contribution => contribution.Status is ProcessStepStatus.InProgress))
        {
            return ProcessStatus.InProgress;
        }

        if (contributions.Any(contribution => contribution.Status is ProcessStepStatus.Pending))
        {
            return contributions.All(contribution => contribution.Status is ProcessStepStatus.Pending)
                ? ProcessStatus.Pending
                : ProcessStatus.InProgress;
        }

        return contributions.Count > 0 &&
            contributions.All(contribution => contribution.Status is ProcessStepStatus.Completed)
                ? ProcessStatus.Completed
                : ProcessStatus.Pending;
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

        return contributions.All(contribution => contribution.Status is ProcessStepStatus.Completed)
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
