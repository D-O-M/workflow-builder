using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;

namespace Backend.ProjectionWorker;

public sealed class ProcessProjectionRepairService
{
    private readonly IBlueprintExecutionCache _blueprintExecutionCache;
    private readonly IProcessInstanceRepository<Dictionary<string, JsonElement>> _processInstanceRepository;
    private readonly IProcessStepRepository _processStepRepository;
    private readonly IProcessIndexProjectionService _processIndexProjectionService;
    private readonly IProcessRealtimeProjectionService _processRealtimeProjectionService;

    public ProcessProjectionRepairService(
        IBlueprintExecutionCache blueprintExecutionCache,
        IProcessInstanceRepository<Dictionary<string, JsonElement>> processInstanceRepository,
        IProcessStepRepository processStepRepository,
        IProcessIndexProjectionService processIndexProjectionService,
        IProcessRealtimeProjectionService processRealtimeProjectionService)
    {
        _blueprintExecutionCache = blueprintExecutionCache;
        _processInstanceRepository = processInstanceRepository;
        _processStepRepository = processStepRepository;
        _processIndexProjectionService = processIndexProjectionService;
        _processRealtimeProjectionService = processRealtimeProjectionService;
    }

    public async Task RepairProcessAsync(
        Guid processId,
        int blueprintVersionId,
        CancellationToken cancellationToken)
    {
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
    }
}
