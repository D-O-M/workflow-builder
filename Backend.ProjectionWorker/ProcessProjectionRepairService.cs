using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;

namespace Backend.ProjectionWorker;

public sealed class ProcessProjectionRepairService
{
    private readonly IBlueprintExecutionCache _blueprintExecutionCache;
    private readonly IProcessInstanceRepository _processInstanceRepository;
    private readonly IProcessStepRepository _processStepRepository;
    private readonly IProcessIndexProjectionService _processIndexProjectionService;
    private readonly IProcessRealtimeProjectionService _processRealtimeProjectionService;
    private readonly ProjectionWorkerTelemetry? _telemetry;

    public ProcessProjectionRepairService(
        IBlueprintExecutionCache blueprintExecutionCache,
        IProcessInstanceRepository processInstanceRepository,
        IProcessStepRepository processStepRepository,
        IProcessIndexProjectionService processIndexProjectionService,
        IProcessRealtimeProjectionService processRealtimeProjectionService,
        ProjectionWorkerTelemetry? telemetry = null)
    {
        _blueprintExecutionCache = blueprintExecutionCache;
        _processInstanceRepository = processInstanceRepository;
        _processStepRepository = processStepRepository;
        _processIndexProjectionService = processIndexProjectionService;
        _processRealtimeProjectionService = processRealtimeProjectionService;
        _telemetry = telemetry;
    }

    public async Task RepairProcessAsync(
        Guid processId,
        int blueprintVersionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var observedAt = DateTimeOffset.UtcNow;
            var blueprint = await _blueprintExecutionCache.GetExecutableBlueprintAsync(
                blueprintVersionId,
                cancellationToken);
            var process = await _processInstanceRepository.GetProcessStateById(
                processId,
                blueprintVersionId,
                cancellationToken);
            var steps = await _processStepRepository.GetProcessStepsForProcess(
                processId,
                cancellationToken);

            await _processIndexProjectionService.UpsertFromProcessAsync(
                process,
                blueprint.DefinitionId,
                blueprint.Id,
                cancellationToken);
            await _processRealtimeProjectionService.UpsertFromProcessAndStepsAsync(
                process,
                blueprint.DefinitionId,
                blueprint.Id,
                steps,
                cancellationToken);
            _telemetry?.RecordProcessed("processes", 1);
            _telemetry?.RecordProcessed("processsteps", steps.Count);
            _telemetry?.RecordLag("processes", process.UpdatedAt ?? process.CreatedAt, observedAt);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _telemetry?.RecordFailure("repair", exception);
            throw;
        }
    }
}
