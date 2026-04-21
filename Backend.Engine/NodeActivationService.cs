using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Interfaces.Queues;
using Backend.Interfaces.Repositories;
using Backend.Models;
using Backend.Plugins.Communication;

namespace Backend.Engine;

public sealed class NodeActivationService
{
    private readonly INodeActivationRepository _nodeActivationRepository;
    private readonly IProcessInstanceRepository<Dictionary<string, JsonElement>> _processInstanceRepository;
    private readonly IProcessStepQueueWriter _processStepQueueWriter;
    private readonly IProcessStepRepository _processStepRepository;
    private readonly ILogger<NodeActivationService> _logger;

    public NodeActivationService(
        INodeActivationRepository nodeActivationRepository,
        IProcessInstanceRepository<Dictionary<string, JsonElement>> processInstanceRepository,
        IProcessStepQueueWriter processStepQueueWriter,
        IProcessStepRepository processStepRepository,
        ILogger<NodeActivationService> logger)
    {
        _nodeActivationRepository = nodeActivationRepository;
        _processInstanceRepository = processInstanceRepository;
        _processStepQueueWriter = processStepQueueWriter;
        _processStepRepository = processStepRepository;
        _logger = logger;
    }

    public async Task EnqueueReadyDownstreamStepsAsync(
        JobContext context,
        PluginExecutionResult executionResult,
        CancellationToken cancellationToken)
    {
        var processState = await _processInstanceRepository.GetProcessStateById(
            context.ProcessId,
            context.ExecutableBlueprintId,
            cancellationToken);

        var nodesById = context.ExecutableBlueprint.NodesById;

        foreach (var outputChannel in context.BlueprintNode.OutputChannels.Values)
        {
            if (!executionResult.OutputData.TryGetValue(outputChannel.Name, out var sourcePayload))
            {
                continue;
            }

            foreach (var binding in outputChannel.OutboundBindings)
            {
                if (!nodesById.TryGetValue(binding.DestinationBlueprintNodeId, out var destinationNode))
                {
                    continue;
                }

                var activationRecord = new NodeActivationRecord
                {
                    id = $"{destinationNode.Id:N}:{binding.Id}",
                    ProcessId = context.ProcessId.ToString(),
                    BlueprintId = context.ExecutableBlueprintId.ToString(),
                    BlueprintNodeId = destinationNode.Id.ToString(),
                    BindingId = binding.Id,
                    SourceProcessStepId = context.ProcessStepId.ToString(),
                    CreatedAt = executionResult.CompletedAt,
                    UpdatedAt = executionResult.CompletedAt,
                    MaterializedInput = BlueprintInputMaterializer.MaterializePartialInput(
                        sourcePayload,
                        binding.PropertyMappings)
                };

                await _nodeActivationRepository.UpsertActivationAsync(activationRecord, cancellationToken);

                var activations = await _nodeActivationRepository.GetNodeActivationsAsync(
                    context.ProcessId,
                    destinationNode.Id,
                    cancellationToken);

                if (activations.Count < destinationNode.InboundBindings.Count)
                {
                    continue;
                }

                processState.NodeOverrides.TryGetValue(destinationNode.Id.ToString(), out var runtimeOverrides);
                var materializedInput = BlueprintInputMaterializer.MergeInputs(
                    activations
                        .OrderBy(activation => activation.BindingId, StringComparer.Ordinal)
                        .Select(activation => (IReadOnlyDictionary<string, JsonElement>)activation.MaterializedInput),
                    destinationNode.DesignTimeInputOverrides,
                    runtimeOverrides);

                var processStepId = ProcessStepIdentity.Create(context.ProcessId, destinationNode.Id);
                await _processStepRepository.CreateProcessStepIfNotExists(
                    new ProcessStepRecord
                    {
                        Id = processStepId.ToString(),
                        BlueprintNodeId = destinationNode.Id.ToString(),
                        TaskDefinitionId = destinationNode.TaskDefinitionId,
                        AllowedMajor = destinationNode.AllowedMajor,
                        AllowedMinor = destinationNode.AllowedMinor,
                        ProcessId = context.ProcessId.ToString(),
                        BlueprintId = context.ExecutableBlueprintId.ToString(),
                        CreatedAt = executionResult.CompletedAt,
                        UpdatedAt = executionResult.CompletedAt,
                        Status = ProcessStepStatus.Pending,
                        InputData = BlueprintInputMaterializer.BuildChannelPayload(
                            destinationNode.InputChannel.Name,
                            materializedInput)
                    },
                    cancellationToken);

                await _processStepQueueWriter.EnqueueAsync(
                    new ProcessStepInitialisationMessage
                    {
                        ProcessId = context.ProcessId,
                        ExecutableBlueprintId = context.ExecutableBlueprintId,
                        ProcessStepId = processStepId,
                        BlueprintNodeId = destinationNode.Id
                    },
                    cancellationToken);

                _logger.LogInformation(
                    "Queued downstream node {BlueprintNodeId} for process {ProcessId} after receiving all inbound bindings.",
                    destinationNode.Id,
                    context.ProcessId);
            }
        }
    }
}
