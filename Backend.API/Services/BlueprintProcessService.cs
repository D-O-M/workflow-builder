using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Interfaces.Queues;
using Backend.Interfaces.Repositories;
using Backend.Database.Relational.Entities;
using Backend.Models;
using Backend.Models.InputModels;
using Backend.Models.ViewModels;
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
    private readonly IBlueprintExecutionCache _blueprintExecutionCache;
    private readonly IBlueprintRepository _blueprintRepository;
    private readonly IProcessInstanceRepository<Dictionary<string, JsonElement>> _processInstanceRepository;
    private readonly ProcessStartTelemetry _processStartTelemetry;
    private readonly ILogger<BlueprintProcessService> _logger;

    public BlueprintProcessService(
        IProcessStepRepository processStepRepository,
        IProcessStepQueueWriter processStepQueueWriter,
        IBlueprintExecutionCache blueprintExecutionCache,
        IBlueprintRepository blueprintRepository,
        IProcessInstanceRepository<Dictionary<string, JsonElement>> processInstanceRepository,
        ProcessStartTelemetry processStartTelemetry,
        ILogger<BlueprintProcessService> logger)
    {
        _processStepRepository = processStepRepository;
        _processStepQueueWriter = processStepQueueWriter;
        _blueprintExecutionCache = blueprintExecutionCache;
        _blueprintRepository = blueprintRepository;
        _processInstanceRepository = processInstanceRepository;
        _processStartTelemetry = processStartTelemetry;
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

        using var totalTiming = _processStartTelemetry.StartTotal(blueprintDefinitionId);
        int latestVersionId;
        using (_processStartTelemetry.StartResolveDefinition(blueprintDefinitionId))
        {
            latestVersionId = await _blueprintRepository.GetLatestBlueprintVersionId(
                blueprintDefinitionId,
                cancellationToken);
        }

        ExecutableBlueprint executableBlueprint;
        using (_processStartTelemetry.StartLoadGraph(blueprintDefinitionId))
        {
            executableBlueprint = await _blueprintExecutionCache.GetExecutableBlueprintAsync(
                latestVersionId,
                cancellationToken);
        }

        using (_processStartTelemetry.StartValidateOverrides(blueprintDefinitionId))
        {
            ValidateNodeOverrides(executableBlueprint, processModel.NodeOverrides);
        }

        _logger.LogInformation(
            "Resolved blueprint definition {BlueprintDefinitionId} to version {BlueprintVersionId}.",
            blueprintDefinitionId,
            latestVersionId);

        var acceptedAt = DateTimeOffset.UtcNow;
        var processId = Guid.NewGuid();
        var initialProcessStepId = ProcessStepIdentity.Create(processId, executableBlueprint.InitialNode.Id);
        var blueprintVersionIdString = latestVersionId.ToString();
        var serializedNodeOverrides = processModel.NodeOverrides.ToDictionary(
            entry => entry.Key.ToString(),
            entry => entry.Value.ToDictionary(
                overrideEntry => overrideEntry.Key,
                overrideEntry => overrideEntry.Value.Clone(),
                StringComparer.Ordinal),
            StringComparer.Ordinal);

        Dictionary<string, JsonElement>? initialNodeOverrides;
        InputHydrationPlan initialHydrationPlan;
        using (_processStartTelemetry.StartMaterializeInitialInput(blueprintDefinitionId))
        {
            serializedNodeOverrides.TryGetValue(executableBlueprint.InitialNode.Id.ToString(), out initialNodeOverrides);
            initialHydrationPlan = CreateInitialHydrationPlan(
                executableBlueprint.InitialNode,
                processModel.InitialTaskInput,
                initialNodeOverrides);
        }

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
            NodeOverrides = serializedNodeOverrides,
            ExecutionProgress = new ProcessExecutionProgress
            {
                TotalNodeCount = executableBlueprint.AllNodes.Count,
                TotalStepCount = executableBlueprint.AllNodes.Count
            }
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
            InputHydrationPlan = initialHydrationPlan
        };

        _logger.LogInformation(
            "Creating process state {ProcessId} for blueprint version {BlueprintVersionId}.",
            processId,
            latestVersionId);
        using (_processStartTelemetry.StartCreateProcessState(blueprintDefinitionId))
        {
            await _processInstanceRepository.CreateProcessState(processState, cancellationToken);
        }

        _logger.LogInformation(
            "Creating initial process step {InitialBlueprintNodeId} for process {ProcessId}.",
            initialProcessStepId,
            processId);
        using (_processStartTelemetry.StartCreateInitialStep(blueprintDefinitionId))
        {
            await _processStepRepository.CreateProcessStep(initialStepState, cancellationToken);
        }

        using (_processStartTelemetry.StartEnqueueInitialStep(blueprintDefinitionId))
        {
            await _processStepQueueWriter.EnqueueAsync(
                new ProcessStepInitialisationMessage
                {
                    ProcessId = processId,
                    ExecutableBlueprintId = latestVersionId,
                    ProcessStepId = initialProcessStepId,
                    BlueprintNodeId = executableBlueprint.InitialNode.Id
                },
                cancellationToken);
        }

        _logger.LogInformation(
            "Accepted process {ProcessId} for blueprint definition {BlueprintDefinitionId}.",
            processId,
            blueprintDefinitionId);

        return new BlueprintProcessAcceptedResult(
            processId,
            blueprintDefinitionId,
            acceptedAt);
    }

    public async Task<BlueprintProcessAcceptedResult> StartVersionAsync(
        int blueprintDefinitionId,
        int blueprintVersionId,
        BlueprintProcessInputModel processModel,
        ProcessPhaseMetadata? phaseMetadata,
        Guid? processIdOverride,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Starting blueprint definition {BlueprintDefinitionId} at version {BlueprintVersionId}.",
            blueprintDefinitionId,
            blueprintVersionId);

        using var totalTiming = _processStartTelemetry.StartTotal(blueprintDefinitionId);
        ExecutableBlueprint executableBlueprint;
        using (_processStartTelemetry.StartLoadGraph(blueprintDefinitionId))
        {
            executableBlueprint = await _blueprintExecutionCache.GetExecutableBlueprintAsync(
                blueprintVersionId,
                cancellationToken);
        }

        if (executableBlueprint.DefinitionId != blueprintDefinitionId)
        {
            throw new InvalidDataException("Blueprint version does not belong to the requested blueprint definition.");
        }

        using (_processStartTelemetry.StartValidateOverrides(blueprintDefinitionId))
        {
            ValidateNodeOverrides(executableBlueprint, processModel.NodeOverrides);
        }

        var acceptedAt = DateTimeOffset.UtcNow;
        var processId = processIdOverride ?? Guid.NewGuid();
        var initialProcessStepId = ProcessStepIdentity.Create(processId, executableBlueprint.InitialNode.Id);
        var blueprintVersionIdString = blueprintVersionId.ToString();
        var serializedNodeOverrides = processModel.NodeOverrides.ToDictionary(
            entry => entry.Key.ToString(),
            entry => entry.Value.ToDictionary(
                overrideEntry => overrideEntry.Key,
                overrideEntry => overrideEntry.Value.Clone(),
                StringComparer.Ordinal),
            StringComparer.Ordinal);

        Dictionary<string, JsonElement>? initialNodeOverrides;
        InputHydrationPlan initialHydrationPlan;
        using (_processStartTelemetry.StartMaterializeInitialInput(blueprintDefinitionId))
        {
            serializedNodeOverrides.TryGetValue(executableBlueprint.InitialNode.Id.ToString(), out initialNodeOverrides);
            initialHydrationPlan = CreateInitialHydrationPlan(
                executableBlueprint.InitialNode,
                processModel.InitialTaskInput,
                initialNodeOverrides);
        }

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
            NodeOverrides = serializedNodeOverrides,
            Phase = phaseMetadata,
            ExecutionProgress = new ProcessExecutionProgress
            {
                TotalNodeCount = executableBlueprint.AllNodes.Count,
                TotalStepCount = executableBlueprint.AllNodes.Count
            }
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
            InputHydrationPlan = initialHydrationPlan
        };

        _logger.LogInformation(
            "Creating process state {ProcessId} for blueprint version {BlueprintVersionId}.",
            processId,
            blueprintVersionId);
        using (_processStartTelemetry.StartCreateProcessState(blueprintDefinitionId))
        {
            await _processInstanceRepository.CreateProcessState(processState, cancellationToken);
        }

        _logger.LogInformation(
            "Creating initial process step {InitialBlueprintNodeId} for process {ProcessId}.",
            initialProcessStepId,
            processId);
        using (_processStartTelemetry.StartCreateInitialStep(blueprintDefinitionId))
        {
            await _processStepRepository.CreateProcessStep(initialStepState, cancellationToken);
        }

        using (_processStartTelemetry.StartEnqueueInitialStep(blueprintDefinitionId))
        {
            await _processStepQueueWriter.EnqueueAsync(
                new ProcessStepInitialisationMessage
                {
                    ProcessId = processId,
                    ExecutableBlueprintId = blueprintVersionId,
                    ProcessStepId = initialProcessStepId,
                    BlueprintNodeId = executableBlueprint.InitialNode.Id
                },
                cancellationToken);
        }

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
                if (!node.InputChannel.Properties.TryGetValue(overrideKey, out var propertyKindName))
                {
                    throw new InvalidDataException(
                        $"Node override '{overrideKey}' does not exist on blueprint node '{nodeId}'.");
                }

                ValidateCollectionInputValue(
                    overrides[overrideKey],
                    propertyKindName,
                    $"Node override '{overrideKey}' on blueprint node '{nodeId}'");
            }
        }
    }

    private static InputHydrationPlan CreateInitialHydrationPlan(
        ExecutableBlueprintNode initialNode,
        IReadOnlyDictionary<string, JsonElement> initialTaskInput,
        IReadOnlyDictionary<string, JsonElement>? runtimeOverrides)
    {
        foreach (var (propertyName, value) in initialTaskInput)
        {
            if (!initialNode.InputChannel.Properties.TryGetValue(propertyName, out var propertyKindName))
            {
                continue;
            }

            ValidateCollectionInputValue(
                value,
                propertyKindName,
                $"Initial input '{propertyName}' on blueprint node '{initialNode.Id}'");
        }

        return new InputHydrationPlan
        {
            InputChannelName = initialNode.InputChannel.Name,
            InitialInput = CloneJsonMap(initialTaskInput),
            DesignTimeInputOverrides = CloneJsonMap(initialNode.DesignTimeInputOverrides),
            RuntimeInputOverrides = CloneJsonMap(runtimeOverrides)
        };
    }

    private static void ValidateCollectionInputValue(
        JsonElement value,
        string propertyKindName,
        string sourceDescription)
    {
        if (!WorkflowPropertyTypeName.TryParse(propertyKindName, out var scalarKind, out var isCollection) ||
            !isCollection)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"{sourceDescription} must be a JSON array for collection type {propertyKindName}.");
        }

        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (!IsCompatibleJsonValue(scalarKind, item))
            {
                throw new InvalidDataException(
                    $"{sourceDescription} item at index {index} is not compatible with type {propertyKindName}.");
            }

            index++;
        }
    }

    private static bool IsCompatibleJsonValue(WorkflowPropertyKind kind, JsonElement value) =>
        kind switch
        {
            WorkflowPropertyKind.String => value.ValueKind == JsonValueKind.String,
            WorkflowPropertyKind.Integer => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            WorkflowPropertyKind.Decimal => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _),
            WorkflowPropertyKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            WorkflowPropertyKind.Guid => value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out _),
            WorkflowPropertyKind.Date => value.ValueKind == JsonValueKind.String && DateOnly.TryParse(value.GetString(), out _),
            WorkflowPropertyKind.DateTime => value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out _),
            WorkflowPropertyKind.Enum => value.ValueKind == JsonValueKind.String,
            _ => false
        };

    private static Dictionary<string, JsonElement> CloneJsonMap(
        IReadOnlyDictionary<string, JsonElement>? values)
    {
        if (values is null)
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        return values.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.Clone(),
            StringComparer.Ordinal);
    }
}
