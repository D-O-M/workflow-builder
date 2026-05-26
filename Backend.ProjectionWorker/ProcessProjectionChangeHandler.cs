using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Models;
using Backend.Repositories;

namespace Backend.ProjectionWorker;

public sealed class ProcessProjectionChangeHandler
{
    private readonly IBlueprintExecutionCache _blueprintExecutionCache;
    private readonly IProcessIndexProjectionService _processIndexProjectionService;
    private readonly IProcessRealtimeProjectionService _processRealtimeProjectionService;
    private readonly ProjectionWorkerTelemetry? _telemetry;
    private readonly ILogger<ProcessProjectionChangeHandler> _logger;

    public ProcessProjectionChangeHandler(
        IBlueprintExecutionCache blueprintExecutionCache,
        IProcessIndexProjectionService processIndexProjectionService,
        IProcessRealtimeProjectionService processRealtimeProjectionService,
        ILogger<ProcessProjectionChangeHandler> logger,
        ProjectionWorkerTelemetry? telemetry = null)
    {
        _blueprintExecutionCache = blueprintExecutionCache;
        _processIndexProjectionService = processIndexProjectionService;
        _processRealtimeProjectionService = processRealtimeProjectionService;
        _telemetry = telemetry;
        _logger = logger;
    }

    public async Task ProjectProcessChangesAsync(
        IReadOnlyCollection<ProcessInstanceRecord> processes,
        CancellationToken cancellationToken)
    {
        var observedAt = DateTimeOffset.UtcNow;
        foreach (var process in processes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryParseBlueprintVersionId(process.BlueprintId, process.ProcessId, out var blueprintVersionId))
            {
                continue;
            }

            var blueprint = await LoadBlueprintAsync(
                blueprintVersionId,
                process.ProcessId,
                cancellationToken);
            if (blueprint is null)
            {
                continue;
            }

            await _processIndexProjectionService.UpsertFromProcessAsync(
                process,
                blueprint.DefinitionId,
                blueprint.Id,
                cancellationToken);
            await _processRealtimeProjectionService.UpsertFromProcessAsync(
                process,
                blueprint.DefinitionId,
                blueprint.Id,
                cancellationToken);
            _telemetry?.RecordProcessed("processes", 1);
            _telemetry?.RecordLag("processes", process.UpdatedAt ?? process.CreatedAt, observedAt);
        }
    }

    public async Task ProjectStepChangesAsync(
        IReadOnlyCollection<ProcessStepRecord> steps,
        CancellationToken cancellationToken)
    {
        var observedAt = DateTimeOffset.UtcNow;
        foreach (var step in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryParseBlueprintVersionId(step.BlueprintId, step.ProcessId, out var blueprintVersionId))
            {
                continue;
            }

            var blueprint = await LoadBlueprintAsync(
                blueprintVersionId,
                step.ProcessId,
                cancellationToken);
            if (blueprint is null)
            {
                continue;
            }

            await _processRealtimeProjectionService.UpsertFromStepAsync(
                step,
                blueprint.DefinitionId,
                blueprint.Id,
                cancellationToken);
            _telemetry?.RecordProcessed("processsteps", 1);
            _telemetry?.RecordLag("processsteps", GetChangedAt(step), observedAt);
        }
    }

    private async Task<ExecutableBlueprint?> LoadBlueprintAsync(
        int blueprintVersionId,
        string processId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _blueprintExecutionCache.GetExecutableBlueprintAsync(
                blueprintVersionId,
                cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
                                         IsMissingEntityException(exception))
        {
            _telemetry?.RecordFailure("blueprints", "missing_blueprint_version");
            _logger.LogWarning(
                exception,
                "Projection worker skipped process {ProcessId} because blueprint version {BlueprintVersionId} could not be loaded.",
                processId,
                blueprintVersionId);
            return null;
        }
    }

    private bool TryParseBlueprintVersionId(
        string blueprintId,
        string processId,
        out int blueprintVersionId)
    {
        if (int.TryParse(blueprintId, out blueprintVersionId))
        {
            return true;
        }

        _logger.LogWarning(
            "Projection worker skipped process {ProcessId} with invalid blueprint version id {BlueprintId}.",
            processId,
            blueprintId);
        _telemetry?.RecordFailure("blueprints", "invalid_blueprint_version_id");
        return false;
    }

    private static DateTimeOffset GetChangedAt(ProcessStepRecord step) =>
        step.UpdatedAt ??
        step.CompletedAt ??
        step.FailedAt ??
        step.StartedAt ??
        step.CreatedAt;

    private static bool IsMissingEntityException(Exception exception) =>
        exception is EntityNotFoundException or KeyNotFoundException ||
        exception.GetType().Name.StartsWith("EntityNotFoundException", StringComparison.Ordinal);
}
