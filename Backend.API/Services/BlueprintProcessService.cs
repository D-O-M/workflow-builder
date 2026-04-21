using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Interfaces.Queues;
using Backend.Interfaces.Repositories;
using Backend.Models;
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

        ValidateNodeOverrides(executableBlueprint, processModel.NodeOverrides);

        _logger.LogInformation(
            "Resolved blueprint definition {BlueprintDefinitionId} to version {BlueprintVersionId}.",
            blueprintDefinitionId,
            latestVersion.BlueprintVersionId);

        var acceptedAt = DateTimeOffset.UtcNow;
        var processId = Guid.NewGuid();
        var initialProcessStepId = ProcessStepIdentity.Create(processId, executableBlueprint.InitialNode.Id);
        var blueprintVersionIdString = latestVersion.BlueprintVersionId.ToString();
        var serializedNodeOverrides = processModel.NodeOverrides.ToDictionary(
            entry => entry.Key.ToString(),
            entry => entry.Value.ToDictionary(
                overrideEntry => overrideEntry.Key,
                overrideEntry => overrideEntry.Value.Clone(),
                StringComparer.Ordinal),
            StringComparer.Ordinal);

        serializedNodeOverrides.TryGetValue(executableBlueprint.InitialNode.Id.ToString(), out var initialNodeOverrides);
        var initialInput = BlueprintInputMaterializer.MergeInputs(
            [(IReadOnlyDictionary<string, JsonElement>)processModel.InitialTaskInput],
            executableBlueprint.InitialNode.DesignTimeInputOverrides,
            initialNodeOverrides);

        var processState = new ProcessInstanceRecord<Dictionary<string, JsonElement>>
        {
            id = processId.ToString(),
            BlueprintId = blueprintVersionIdString,
            CreatedAt = acceptedAt,
            UpdatedAt = acceptedAt,
            Status = ProcessStatus.Pending,
            GlobalState = processModel.GlobalState.ToDictionary(
                entry => entry.Key,
                entry => entry.Value.Clone(),
                StringComparer.Ordinal),
            NodeOverrides = serializedNodeOverrides
        };

        var initialStepState = new ProcessStepRecord
        {
            Id = initialProcessStepId.ToString(),
            BlueprintNodeId = executableBlueprint.InitialNode.Id.ToString(),
            TaskDefinitionId = executableBlueprint.InitialNode.TaskDefinitionId,
            AllowedMajor = executableBlueprint.InitialNode.AllowedMajor,
            AllowedMinor = executableBlueprint.InitialNode.AllowedMinor,
            ProcessId = processId.ToString(),
            BlueprintId = blueprintVersionIdString,
            CreatedAt = acceptedAt,
            UpdatedAt = acceptedAt,
            Status = ProcessStepStatus.Pending,
            InputData = BlueprintInputMaterializer.BuildChannelPayload(
                executableBlueprint.InitialNode.InputChannel.Name,
                initialInput)
        };

        _logger.LogInformation(
            "Creating process state {ProcessId} for blueprint version {BlueprintVersionId}.",
            processId,
            latestVersion.BlueprintVersionId);
        await _processInstanceRepository.CreateProcessState(processState, cancellationToken);

        _logger.LogInformation(
            "Creating initial process step {InitialBlueprintNodeId} for process {ProcessId}.",
            initialProcessStepId,
            processId);
        await _processStepRepository.CreateProcessStep(initialStepState, cancellationToken);

        await _processStepQueueWriter.EnqueueAsync(
            new ProcessStepInitialisationMessage
            {
                ProcessId = processId,
                ExecutableBlueprintId = latestVersion.BlueprintVersionId,
                ProcessStepId = initialProcessStepId,
                BlueprintNodeId = executableBlueprint.InitialNode.Id
            },
            cancellationToken);

        _logger.LogInformation(
            "Accepted process {ProcessId} for blueprint definition {BlueprintDefinitionId}.",
            processId,
            blueprintDefinitionId);

        return new BlueprintProcessAcceptedResult(
            processId,
            blueprintDefinitionId,
            acceptedAt);
    }

    private static void ValidateNodeOverrides(
        ExecutableBlueprint executableBlueprint,
        IReadOnlyDictionary<Guid, Dictionary<string, JsonElement>> nodeOverrides)
    {
        if (nodeOverrides.Count == 0)
        {
            return;
        }

        var nodesById = executableBlueprint.NodesById;
        foreach (var (nodeId, overrides) in nodeOverrides)
        {
            if (!nodesById.TryGetValue(nodeId, out var node))
            {
                throw new InvalidDataException($"Node override references unknown blueprint node '{nodeId}'.");
            }

            foreach (var overrideKey in overrides.Keys)
            {
                if (!node.InputChannel.Properties.ContainsKey(overrideKey))
                {
                    throw new InvalidDataException(
                        $"Node override '{overrideKey}' does not exist on blueprint node '{nodeId}'.");
                }
            }
        }
    }
}
