using System.Runtime.CompilerServices;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Models.InputModels;
using Backend.Repositories;
using Backend.Services.Recovery;

namespace Backend.API.Services;

public sealed record RecoverySelection(Guid? ProcessId, Guid? RuntimeStepId,
    ProcessSearchRequestInputModel? Search, IReadOnlyList<Guid>? SelectedProcessIds,
    IReadOnlyList<Guid>? ExcludedProcessIds, bool AllMatches = false);

public sealed record RecoveryEdit(int BlueprintVersionId, Guid BlueprintNodeId, string? Action,
    bool? Continuation, Guid? ProcessId, Guid? StepId, Guid? ReuseStepId);

public sealed record RecoveryRevision(int Revision, string? NewWorkPolicy, IReadOnlyList<RecoveryEdit>? Edits);
public sealed record RecoveryConfirmation(int Revision, string Reason);

public sealed class ProcessRecoveryService(IProcessRecoveryStore store, ProcessQueryService queries,
    IProcessInstanceRepository processes, IProcessStepRepository steps, IBlueprintExecutionCache blueprints)
{
    public async Task<RecoveryBatch> PreviewAsync(RecoverySelection selection, string actor, CancellationToken ct)
    {
        var batch = new RecoveryBatch { BatchId = Guid.NewGuid(), Stamp = Guid.NewGuid(),
            Actor = actor, CreatedAt = DateTimeOffset.UtcNow };
        int ordinal = 0, targetCount = 0, eligible = 0;
        await foreach (var member in SelectAsync(selection, ct))
        {
            var item = new RecoveryCase { Id = ProcessRecoveryStore.CaseId(batch.Stamp, member.ProcessId),
                BatchId = batch.BatchId, Stamp = batch.Stamp, ProcessId = member.ProcessId, Ordinal = ordinal++,
                BlueprintDefinitionId = member.DefinitionId, BlueprintVersionId = member.VersionId,
                Plan = new() { ProcessId = member.ProcessId, BlueprintVersionId = member.VersionId } };
            try
            {
                item = await PlanMemberAsync(selection, member, item, ct);
            }
            catch (EntityNotFoundException)
            {
                item = item with { Status = "Changed", Message = "The selected Process or its Blueprint version is no longer available." };
            }
            await store.StageAsync(item, ct);
            if (CanConfirm(item)) eligible++;
            targetCount += item.Plan.TargetStepIds.Count;
        }
        batch = batch with { ProcessCount = ordinal, StepCount = targetCount, EligibleProcessCount = eligible };
        await store.CreateAsync(batch, ct);
        return batch;
    }

    private async Task<RecoveryCase> PlanMemberAsync(RecoverySelection selection, Member member, RecoveryCase item, CancellationToken ct)
    {
        var process = await processes.GetProcessStateById(member.ProcessId, member.VersionId, ct);
        var records = await steps.GetProcessStepsForProcess(member.ProcessId, ct);
        var effective = ProcessRecoveryState.EffectiveSteps(process, records);
        var targets = selection.RuntimeStepId is Guid id
            ? effective.Where(s => s.Id == id.ToString() && s.Status == ProcessStepStatus.Failed).Select(s => id).ToList()
            : ProcessQueryService.FindFailedRecoverySteps(selection.Search!, member.DefinitionId, member.VersionId, effective);
        if (!member.Matched || targets.Count == 0 || (selection.Search is { } search &&
            !ProcessQueryService.MatchesRecoveryProcessScope(search, member.DefinitionId, process)))
            return item with { Status = process.Recovery?.Outcome == "Completed" && targets.Count == 0 ? "AlreadyRecovered" : "Changed",
                Message = "The current Process or its failed Steps no longer match this selection." };
        var graph = await blueprints.GetExecutableBlueprintAsync(member.VersionId, ct);
        return item with { Plan = ProcessRecoveryPlanner.Build(graph, process, records, targets) };
    }

    public async Task<RecoveryBatch> ReviseAsync(Guid id, RecoveryRevision revision, string actor, CancellationToken ct)
    {
        var previous = await GetAsync(id, ct);
        if (previous.Revision != revision.Revision || previous.Status == "Confirmed")
            throw new RecoveryConflictException("Review the current revision before changing this recovery.");
        if (revision.NewWorkPolicy is not (null or "Run" or "Pause"))
            throw new ArgumentException("New work policy must be Pause or Run.");
        var edits = revision.Edits ?? [];
        if (edits.Any(e => e.Action is not (null or "Run" or "Skip" or "Reuse") ||
            (e.StepId.HasValue && !e.ProcessId.HasValue) || (e.ReuseStepId.HasValue && !e.StepId.HasValue)))
            throw new ArgumentException("Specify a supported action and identify the Process and Step for an occurrence override.");

        var next = previous with { Revision = checked(previous.Revision + 1), Stamp = Guid.NewGuid(),
            NewWorkPolicy = revision.NewWorkPolicy ?? previous.NewWorkPolicy,
            Status = "Preview", Actor = actor, Reason = null, ConfirmedAt = null, NextPoll = 0 };
        int revisable = 0, eligible = 0;
        for (int offset = 0; offset < previous.ProcessCount; offset += 25)
        {
            foreach (var old in await store.PageAsync(previous, offset, 25, ct))
            {
                var item = old with { Id = ProcessRecoveryStore.CaseId(next.Stamp, old.ProcessId), Stamp = next.Stamp,
                    ETag = null, NextPoll = 0 };
                if (old.Status is "Preview" or "Held" or "Failed")
                {
                    revisable++;
                    try
                    {
                        item = await ReviseCaseAsync(item, previous, next.NewWorkPolicy, edits, ct);
                    }
                    catch (EntityNotFoundException)
                    {
                        item = item with { Status = "Changed", Message = "The Process or its Blueprint version is no longer available." };
                    }
                }
                await store.StageAsync(item, ct);
                if (CanConfirm(item)) eligible++;
            }
        }
        if (revisable == 0)
            throw new RecoveryConflictException("There are no outstanding cases in this recovery.");
        next = next with { EligibleProcessCount = eligible };
        if (!await store.ReplaceAsync(next, ct))
            throw new RecoveryConflictException("Another operator changed this recovery. Reload its preview.");
        return next;
    }

    private async Task<RecoveryCase> ReviseCaseAsync(RecoveryCase item, RecoveryBatch previous, string? newPolicy,
        IReadOnlyList<RecoveryEdit> edits, CancellationToken ct)
    {
        var process = await processes.GetProcessStateById(item.ProcessId, item.BlueprintVersionId, ct);
        var records = await steps.GetProcessStepsForProcess(item.ProcessId, ct);
        var effective = ProcessRecoveryState.EffectiveSteps(process, records);
        bool resuming = previous.Status == "Completed";
        if ((!resuming && item.Plan.ProcessETag != process.ETag) ||
            (resuming && process.Recovery is { } owner && owner.RecoveryId != previous.BatchId))
            return item with { Status = "Changed", Message = "The Process changed after this preview." };

        var original = item.Plan.Steps.Where(s => item.Plan.TargetStepIds.Contains(s.StepId)).Select(s => s.LogicalStepId).ToHashSet();
        var targets = effective.Where(s => s.Status == ProcessStepStatus.Failed &&
            (original.Contains(Guid.Parse(s.LogicalStepId ?? s.Id)) || (resuming && s.RecoveryId == previous.BatchId)))
            .Select(s => Guid.Parse(s.Id)).ToList();
        if (targets.Count == 0) return item with { Status = "AlreadyRecovered", Message = "The selected failures have been resolved." };
        var graph = await blueprints.GetExecutableBlueprintAsync(item.BlueprintVersionId, ct);
        var current = resuming ? ProcessRecoveryPlanner.Build(graph, process, records, targets) : item.Plan;
        var actions = current.Steps.Where(s => s.ActionExplicit).ToDictionary(s => s.StepId, s => s.Action);
        var sources = current.Steps.Where(s => s.ReuseStepId.HasValue).ToDictionary(s => s.StepId, s => s.ReuseStepId!.Value);
        var anchors = current.ContinuationStepIds.ToHashSet();
        foreach (var edit in edits.Where(e => e.BlueprintVersionId == item.BlueprintVersionId &&
            (!e.ProcessId.HasValue || e.ProcessId == item.ProcessId)).OrderBy(e => e.ProcessId.HasValue).ThenBy(e => e.StepId.HasValue))
        {
            foreach (var step in current.Steps.Where(s => s.BlueprintNodeId == edit.BlueprintNodeId && (!edit.StepId.HasValue || edit.StepId == s.StepId)))
            {
                if (edit.Continuation == true && !step.CanStartHere)
                {
                    if (edit.StepId.HasValue) throw new ArgumentException("This Step is outside the selected failures' recorded predecessors.");
                    continue;
                }
                if (edit.Action is not null) actions[step.StepId] = edit.Action;
                if (edit.ReuseStepId is Guid source) sources[step.StepId] = source;
                if (edit.Continuation == true) anchors.Add(step.StepId);
                if (edit.Continuation == false) anchors.Remove(step.StepId);
            }
        }
        var plan = ProcessRecoveryPlanner.Build(graph, process, records, targets, anchors.ToList(), actions,
            newPolicy ?? item.Plan.NewWorkPolicy, sources);
        return item with { Plan = plan, Status = "Preview", Message = null };
    }

    public async Task<RecoveryBatch> ConfirmAsync(Guid id, RecoveryConfirmation request, string actor, CancellationToken ct)
    {
        var batch = await GetAsync(id, ct);
        if (request.Revision != batch.Revision) throw new RecoveryConflictException("The preview revision has changed.");
        if (batch.ConfirmedAt.HasValue) return batch;
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 2000)
            throw new ArgumentException("Enter a reason of up to 2000 characters.");
        if (batch.EligibleProcessCount == 0) throw new RecoveryConflictException("There are no eligible failed Steps in this revision.");
        return await store.ConfirmAsync(batch with { Status = "Confirmed", Reason = request.Reason.Trim(),
            Actor = actor, ConfirmedAt = DateTimeOffset.UtcNow, ConfirmedRevisions = [.. batch.ConfirmedRevisions, batch.Revision] }, ct);
    }

    public async Task<RecoveryBatch> GetAsync(Guid id, CancellationToken ct) =>
        await store.ReadAsync(id, ct) ?? throw new EntityNotFoundException<RecoveryBatch>(id.ToString());

    public async Task<RecoveryBatch> GetRevisionAsync(Guid id, int? revision, CancellationToken ct)
    {
        var current = await GetAsync(id, ct);
        if (!revision.HasValue || current.Revision == revision.Value) return current;
        var snapshot = await store.ReadConfirmationAsync(id, revision.Value, ct)
            ?? throw new EntityNotFoundException<RecoveryBatch>(revision.Value);
        return snapshot with { ConfirmedRevisions = current.ConfirmedRevisions };
    }

    public async Task<object> PageAsync(RecoveryBatch batch, int offset, int limit, CancellationToken ct)
    {
        ValidatePage(offset, limit);
        var page = await store.PageAsync(batch, offset, limit, ct);
        return new { id = batch.BatchId, batch.Revision, batch.Status, batch.ProcessCount, batch.EligibleProcessCount, batch.StepCount, batch.NewWorkPolicy,
            batch.CreatedAt, batch.ConfirmedAt, batch.ConfirmedRevisions, batch.Actor, batch.Reason,
            cases = page.Select(c => new { c.ProcessId, c.BlueprintDefinitionId, c.BlueprintVersionId,
                status = c.Status == "Preview" && batch.Status == "Confirmed" ? "Pending" : c.Status,
                c.Message, c.Plan }), hasMore = (long)offset + page.Count < batch.ProcessCount };
    }

    public async Task<object> HistoryAsync(Guid? processId, int offset, int limit, CancellationToken ct)
    {
        ValidatePage(offset, limit);
        List<RecoveryBatch> batches;
        if (processId.HasValue)
        {
            batches = [];
            await foreach (var id in store.FindForProcessAsync(processId.Value, ct))
                if (await store.ReadAsync(id, ct) is { } batch) batches.Add(batch);
            batches = batches.OrderByDescending(b => b.CreatedAt).Skip(offset).Take(limit).ToList();
        }
        else batches = await store.RecentAsync(offset, limit, ct);
        return batches.Select(b => new { id = b.BatchId, b.Revision, b.Status, b.ProcessCount, b.StepCount,
            b.CreatedAt, b.Actor, b.Reason, b.ConfirmedAt });
    }

    private static void ValidatePage(int offset, int limit)
    {
        if (offset < 0 || limit is < 1 or > 100) throw new ArgumentException("Use a non-negative offset and a limit from 1 to 100.");
    }

    private static bool CanConfirm(RecoveryCase item) =>
        item.Status == "Preview" && item.Plan.TargetStepIds.Count > 0 && item.Plan.Issues.Count == 0;

    private async IAsyncEnumerable<Member> SelectAsync(RecoverySelection selection, [EnumeratorCancellation] CancellationToken ct)
    {
        var selected = (selection.SelectedProcessIds ?? []).ToHashSet();
        var excluded = (selection.ExcludedProcessIds ?? []).ToHashSet();
        if (selection.ProcessId is Guid id)
        {
            if (!selection.RuntimeStepId.HasValue || selection.Search != null || selected.Count != 0 || excluded.Count != 0 || selection.AllMatches)
                throw new ArgumentException("Identify one failed runtime Step or select a search cohort.");
            // The Step partition is authoritative even before the Process directory projection exists.
            var step = await steps.GetProcessStepById(selection.RuntimeStepId.Value, id, ct);
            var version = int.Parse(step.BlueprintId, System.Globalization.CultureInfo.InvariantCulture);
            var blueprint = await blueprints.GetExecutableBlueprintAsync(version, ct);
            yield return new(id, blueprint.DefinitionId, version, true);
            yield break;
        }
        var search = selection.Search ?? throw new ArgumentException("Supply the Advanced Search filters.");
        if (selection.RuntimeStepId.HasValue || (!selection.AllMatches && (selected.Count == 0 || excluded.Count != 0))
            || (selection.AllMatches && selected.Count != 0))
            throw new ArgumentException("Choose selected Processes or all search matches with exclusions.");
        var seen = new HashSet<Guid>();
        string? cursor = null;
        do
        {
            var page = await queries.SearchProcessesAsync(new ProcessSearchRequestInputModel
            {
                BlueprintDefinitionIds = search.BlueprintDefinitionIds, BlueprintFilters = search.BlueprintFilters,
                ProcessStatuses = search.ProcessStatuses, CreatedFrom = search.CreatedFrom, CreatedTo = search.CreatedTo,
                NodeFilters = search.NodeFilters, PropertyFilters = search.PropertyFilters, Cursor = cursor, Limit = 50
            }, ct);
            foreach (var row in page.Items)
            {
                if (excluded.Contains(row.ProcessId) || (!selection.AllMatches && !selected.Remove(row.ProcessId))
                    || !seen.Add(row.ProcessId)) continue;
                yield return new(row.ProcessId, row.BlueprintDefinitionId, row.BlueprintVersionId, true);
            }
            cursor = page.NextCursor;
        } while (cursor != null && (selection.AllMatches || selected.Count != 0));
        foreach (var leftover in selected)
        {
            Member missing;
            try
            {
                var detail = await queries.GetProcessDetailByIdAsync(leftover, ct);
                missing = new(leftover, detail.BlueprintDefinitionId, detail.BlueprintVersionId, false);
            }
            catch (EntityNotFoundException)
            {
                missing = new(leftover, 0, 0, false);
            }
            yield return missing;
        }
    }

    private sealed record Member(Guid ProcessId, int DefinitionId, int VersionId, bool Matched);
}
