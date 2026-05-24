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
    private readonly ILogger<ProcessProjectionChangeHandler> _logger;

    public ProcessProjectionChangeHandler(
        IBlueprintExecutionCache blueprintExecutionCache,
        IProcessIndexProjectionService processIndexProjectionService,
        IProcessRealtimeProjectionService processRealtimeProjectionService,
        ILogger<ProcessProjectionChangeHandler> logger)
    {
        _blueprintExecutionCache = blueprintExecutionCache;
        _processIndexProjectionService = processIndexProjectionService;
        _processRealtimeProjectionService = processRealtimeProjectionService;
        _logger = logger;
    }

    public async Task ProjectProcessChangesAsync(
        IReadOnlyCollection<ProcessInstanceRecord<Dictionary<string, JsonElement>>> processes,
        CancellationToken cancellationToken)
    {
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
        }
    }

    public async Task ProjectStepChangesAsync(
        IReadOnlyCollection<ProcessStepRecord> steps,
        CancellationToken cancellationToken)
    {
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
        return false;
    }

    private static bool IsMissingEntityException(Exception exception) =>
        exception is EntityNotFoundException or KeyNotFoundException ||
        exception.GetType().Name.StartsWith("EntityNotFoundException", StringComparison.Ordinal);
}
