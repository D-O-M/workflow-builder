using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Interfaces.Queues;
using Backend.Interfaces.Repositories;
using Backend.Models.InputModels;
using Backend.Plugins.Communication;
using Microsoft.Extensions.Logging;

namespace Backend.API.Services;

public sealed record BlueprintProcessAcceptedResult(
    Guid ProcessId,
    int BlueprintId,
    DateTimeOffset AcceptedAt);

public sealed class BlueprintProcessService
{
    private readonly IProcessStepRepository _processStepRepository;
    private readonly IProcessStepQueueWriter _processStepQueueWriter;
    private readonly IBlueprintRepository _blueprintRepository;
    private readonly IProcessInstanceRepository<Dictionary<string, JsonElement>> _processInstanceRepository;
    private readonly ILogger<BlueprintProcessService> _logger;

    public BlueprintProcessService(
        IProcessStepRepository processStepRepository,
        IProcessStepQueueWriter processStepQueueWriter,
        IBlueprintRepository blueprintRepository,
        IProcessInstanceRepository<Dictionary<string, JsonElement>> processInstanceRepository,
        ILogger<BlueprintProcessService> logger)
    {
        _processStepRepository = processStepRepository;
        _processStepQueueWriter = processStepQueueWriter;
        _blueprintRepository = blueprintRepository;
        _processInstanceRepository = processInstanceRepository;
        _logger = logger;
    }

    public async Task<BlueprintProcessAcceptedResult> StartLatestVersionAsync(
        int blueprintDefinitionId,
        BlueprintProcessInputModel processModel,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Starting latest blueprint version for definition {BlueprintDefinitionId}.",
            blueprintDefinitionId);

        var blueprint = await _blueprintRepository.GetBlueprintById(
            blueprintDefinitionId,
            cancellationToken);

        var latestVersion = blueprint.Versions
            .OrderByDescending(version => version.Version)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"Blueprint {blueprintDefinitionId} has no versions to start.");

        var executableBlueprint = await _blueprintRepository.GetFullBlueprintById(
            latestVersion.BlueprintVersionId,
            cancellationToken);

        _logger.LogInformation(
            "Resolved blueprint definition {BlueprintDefinitionId} to version {BlueprintVersionId}.",
            blueprintDefinitionId,
            latestVersion.BlueprintVersionId);

        var acceptedAt = DateTimeOffset.UtcNow;
        var processId = Guid.NewGuid();
        var seedBlueprintNodeId = Guid.NewGuid();
        var firstBlueprintNodeId = Guid.NewGuid();
        var blueprintVersionIdString = latestVersion.BlueprintVersionId.ToString();

        var processState = new ProcessInstanceRecord<Dictionary<string, JsonElement>>
        {
            id = processId.ToString(),
            BlueprintId = blueprintVersionIdString,
            CreatedAt = acceptedAt,
            UpdatedAt = acceptedAt,
            Status = ProcessStatus.Pending,
            GlobalState = processModel.GlobalState
        };

        var firstInputPayload = JsonSerializer.SerializeToElement(
            processModel.InitialTaskInput,
            JsonSerializerOptions.Web);

        var triggerSeedState = new ProcessStepRecord
        {
            Id = seedBlueprintNodeId.ToString(),
            BlueprintNodeId = Guid.Empty.ToString(),
            ProcessId = processId.ToString(),
            BlueprintId = blueprintVersionIdString,
            CreatedAt = acceptedAt,
            UpdatedAt = acceptedAt,
            CompletedAt = acceptedAt,
            Status = ProcessStepStatus.Completed,
            OutputData = new Dictionary<string, JsonElement>
            {
                [executableBlueprint.InitialNode.InputChannel.Name] = firstInputPayload
            }
        };

        var initialStepState = new ProcessStepRecord
        {
            Id = firstBlueprintNodeId.ToString(),
            BlueprintNodeId = executableBlueprint.InitialNode.Id.ToString(),
            TaskDefinitionId = executableBlueprint.InitialNode.TaskDefinitionId,
            AllowedMajor = executableBlueprint.InitialNode.AllowedMajor,
            AllowedMinor = executableBlueprint.InitialNode.AllowedMinor,
            ProcessId = processId.ToString(),
            BlueprintId = blueprintVersionIdString,
            CreatedAt = acceptedAt,
            UpdatedAt = acceptedAt,
            Status = ProcessStepStatus.Pending
        };

        _logger.LogInformation(
            "Creating process state {ProcessId} for blueprint version {BlueprintVersionId}.",
            processId,
            latestVersion.BlueprintVersionId);
        await _processInstanceRepository.CreateProcessState(processState, cancellationToken);

        _logger.LogInformation(
            "Creating seed process step {SeedBlueprintNodeId} for process {ProcessId}.",
            seedBlueprintNodeId,
            processId);
        await _processStepRepository.CreateProcessStep(triggerSeedState, cancellationToken);

        _logger.LogInformation(
            "Creating initial process step {InitialBlueprintNodeId} for process {ProcessId}.",
            firstBlueprintNodeId,
            processId);
        await _processStepRepository.CreateProcessStep(initialStepState, cancellationToken);

        var initialisationMessage = new ProcessStepInitialisationMessage
        {
            ProcessId = processId,
            ExecutableBlueprintId = latestVersion.BlueprintVersionId,
            ProcessStepId = firstBlueprintNodeId,
            BlueprintNodeId = executableBlueprint.InitialNode.Id,
            PreviousProcessStepId = seedBlueprintNodeId,
            LinkedOutputChannelName = executableBlueprint.InitialNode.InputChannel.Name
        };

        _logger.LogInformation(
            "Enqueuing initialisation message for process {ProcessId}, process step {ProcessStepId}.",
            processId,
            firstBlueprintNodeId);
        await _processStepQueueWriter.EnqueueAsync(initialisationMessage, cancellationToken);

        _logger.LogInformation(
            "Accepted process {ProcessId} for blueprint definition {BlueprintDefinitionId}.",
            processId,
            blueprintDefinitionId);

        return new BlueprintProcessAcceptedResult(
            processId,
            blueprintDefinitionId,
            acceptedAt);
    }
}
