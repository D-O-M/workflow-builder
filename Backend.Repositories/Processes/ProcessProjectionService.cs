using System.Text.Json;
using Backend.Database.Relational;
using Backend.Database.Relational.Entities;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Models.Realtime;
using Backend.Models.Versioning;
using Backend.Models.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace Backend.Repositories.Processes;

public sealed class ProcessProjectionService
{
    private readonly BlueprintDbContext _dbContext;
    private readonly IProcessStepRepository _processStepRepository;
    private readonly IProcessInstanceRepository<Dictionary<string, JsonElement>> _processInstanceRepository;
    private readonly IProcessIndexProjectionService? _processIndexProjectionService;

    public ProcessProjectionService(
        BlueprintDbContext dbContext,
        IProcessStepRepository processStepRepository,
        IProcessInstanceRepository<Dictionary<string, JsonElement>> processInstanceRepository,
        IProcessIndexProjectionService? processIndexProjectionService = null)
    {
        _dbContext = dbContext;
        _processStepRepository = processStepRepository;
        _processInstanceRepository = processInstanceRepository;
        _processIndexProjectionService = processIndexProjectionService;
    }

    public async Task<List<ProcessSummaryViewModel>> GetProcessesForBlueprintDefinitionAsync(
        int blueprintDefinitionId,
        CancellationToken cancellationToken)
    {
        var definition = await _dbContext.BlueprintDefinitions
            .Include(d => d.Versions)
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == blueprintDefinitionId, cancellationToken);

        if (definition is null)
        {
            throw new EntityNotFoundException<BlueprintViewModel>(blueprintDefinitionId);
        }

        var summaries = new List<ProcessSummaryViewModel>();
        foreach (var version in definition.Versions.OrderByDescending(v => v.Version))
        {
            var processRecords = await _processInstanceRepository.GetProcessStatesForBlueprint(
                version.Id,
                cancellationToken);

            foreach (var processRecord in processRecords)
            {
                var tasks = await _processStepRepository.GetProcessStepsForProcess(
                    Guid.Parse(processRecord.ProcessId),
                    cancellationToken);

                summaries.Add(BuildSummary(definition.Id, definition.DisplayName, version, processRecord, tasks));
            }
        }

        return summaries
            .OrderByDescending(summary => summary.CreatedAt)
            .ToList();
    }

    public async Task<ProcessDetailViewModel> GetProcessDetailAsync(
        int blueprintDefinitionId,
        Guid processId,
        CancellationToken cancellationToken)
    {
        var context = await LoadProcessContextAsync(
            blueprintDefinitionId,
            processId,
            cancellationToken);

        return BuildDetail(
            context.Definition,
            context.BlueprintVersion,
            context.ProcessRecord,
            context.ProcessSteps);
    }

    public async Task<ProcessDetailViewModel> GetProcessDetailByIdAsync(
        Guid processId,
        CancellationToken cancellationToken)
    {
        var context = await LoadProcessContextByIdAsync(
            processId,
            cancellationToken);

        return BuildDetail(
            context.Definition,
            context.BlueprintVersion,
            context.ProcessRecord,
            context.ProcessSteps);
    }

    public async Task<ProcessStepDetailViewModel> GetProcessStepDetailAsync(
        int blueprintDefinitionId,
        Guid processId,
        Guid blueprintNodeId,
        CancellationToken cancellationToken)
    {
        var context = await LoadProcessContextAsync(
            blueprintDefinitionId,
            processId,
            cancellationToken);
        var blueprintNode = context.BlueprintVersion.BlueprintNodes
            .FirstOrDefault(candidate => candidate.Id == blueprintNodeId)
            ?? throw new EntityNotFoundException<ProcessStepDetailViewModel>(blueprintNodeId.ToString());
        context.ProcessStepsByBlueprintNodeId.TryGetValue(blueprintNode.Id, out var taskStates);
        var taskState = SelectRepresentativeStep(taskStates);
        var actualResolvedVersion = ResolveActualVersion(blueprintNode, taskState);
        var endedAt = CalculateAggregateEndedAt(taskStates);
        var sourceNameByBlueprintNodeId = context.BlueprintVersion.BlueprintNodes
            .ToDictionary(node => node.Id, GetNodeDisplayName);
        var designTimeOverrides = SerializePayloads(DeserializePayloads(blueprintNode.DesignTimeInputOverridesJson));
        var runtimeOverrides = context.ProcessRecord.NodeOverrides.TryGetValue(blueprintNode.Id.ToString(), out var nodeOverrides)
            ? SerializePayloads(nodeOverrides)
            : null;

        return new ProcessStepDetailViewModel
        {
            BlueprintNodeId = blueprintNode.Id,
            Name = GetNodeDisplayName(blueprintNode),
            NodeKind = blueprintNode.NodeKind.ToString(),
            CollectionIteratorItemKind = blueprintNode.CollectionIteratorItemKind,
            RuntimeStepCount = taskStates?.Count ?? 0,
            Iterations = BuildIterationViewModels(taskStates),
            Status = MapAggregateStatus(taskStates),
            ResolvedVersion = taskState?.ResolvedTaskVersion ?? actualResolvedVersion?.Version.ToString() ?? "Native",
            StartedAt = CalculateAggregateStartedAt(taskStates),
            EndedAt = endedAt,
            DurationMs = CalculateDurationMs(CalculateAggregateStartedAt(taskStates), endedAt),
            FailureMessage = taskStates?.FirstOrDefault(step => step.Status == ProcessStepStatus.Failed)?.ErrorMessage,
            Inputs = SerializePayloads(taskState?.InputData),
            Outputs = SerializePayloads(taskState?.OutputData),
            IncomingMappings = blueprintNode.IncomingConnections
                .Where(connection => connection.SourceBlueprintNodeId.HasValue)
                .OrderBy(connection => sourceNameByBlueprintNodeId.TryGetValue(connection.SourceBlueprintNodeId!.Value, out var name) ? name : connection.SourceBlueprintNodeId.ToString())
                .Select(connection => new ProcessStepIncomingMappingViewModel
                {
                    SourceBlueprintNodeId = connection.SourceBlueprintNodeId!.Value,
                    SourceName = sourceNameByBlueprintNodeId.TryGetValue(connection.SourceBlueprintNodeId!.Value, out var name)
                        ? name
                        : $"Node {connection.SourceBlueprintNodeId}",
                    ChannelName = connection.ChannelName,
                    PropertyMappings = DeserializePropertyMappings(connection.PropertyMappingsJson),
                })
                .ToList(),
            DesignTimeInputOverrides = designTimeOverrides,
            RuntimeOverrides = runtimeOverrides,
        };
    }

    public async Task<Guid?> GetRuntimeProcessStepIdAsync(
        int blueprintDefinitionId,
        Guid processId,
        Guid blueprintNodeId,
        CancellationToken cancellationToken)
    {
        var context = await LoadProcessContextAsync(
            blueprintDefinitionId,
            processId,
            cancellationToken);
        var blueprintNode = context.BlueprintVersion.BlueprintNodes
            .FirstOrDefault(candidate => candidate.Id == blueprintNodeId)
            ?? throw new EntityNotFoundException<ProcessStepDetailViewModel>(blueprintNodeId.ToString());

        if (!context.ProcessStepsByBlueprintNodeId.TryGetValue(blueprintNode.Id, out var taskStates))
        {
            return null;
        }

        var taskState = SelectRepresentativeStep(taskStates);
        if (taskState is null)
        {
            return null;
        }

        return Guid.TryParse(taskState.Id, out var runtimeProcessStepId)
            ? runtimeProcessStepId
            : null;
    }

    public async Task<ProcessRealtimeBatch?> BuildRealtimeBatchAsync(
        int blueprintVersionId,
        int blueprintDefinitionId,
        Guid processId,
        IReadOnlyCollection<Guid> changedBlueprintNodeIds,
        bool includeSummary,
        bool includeNodeUpdates,
        CancellationToken cancellationToken)
    {
        if (!includeSummary && (!includeNodeUpdates || changedBlueprintNodeIds.Count == 0))
        {
            return null;
        }

        var processRecord = await _processInstanceRepository.GetProcessStateById(
            processId,
            blueprintVersionId,
            cancellationToken);
        var processSteps = await _processStepRepository.GetProcessStepsForProcess(
            processId,
            cancellationToken);

        var versionMetadata = await _dbContext.BlueprintVersions
            .AsNoTracking()
            .Where(version => version.Id == blueprintVersionId)
            .Select(version => new
            {
                version.Id,
                version.Version,
                BlueprintName = version.BlueprintDefinition.DisplayName,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (versionMetadata is null)
        {
            throw new EntityNotFoundException<BlueprintVersionViewModel>(blueprintVersionId);
        }

        var batch = new ProcessRealtimeBatch();

        if (includeSummary)
        {
            batch.Summaries.Add(BuildSummaryEvent(
                blueprintDefinitionId,
                versionMetadata.BlueprintName,
                versionMetadata.Id,
                versionMetadata.Version,
                processRecord,
                processSteps));
        }

        if (includeNodeUpdates && changedBlueprintNodeIds.Count > 0)
        {
            var deltaTasks = await LoadBlueprintNodesForDeltasAsync(
                blueprintVersionId,
                changedBlueprintNodeIds,
                cancellationToken);

            if (deltaTasks.Count > 0)
            {
                var processStepsByBlueprintNodeId = GroupProcessStepsByBlueprintNodeId(processSteps);

                var nodeDeltas = deltaTasks
                    .Select(blueprintNode => BuildNodeDelta(blueprintNode, processStepsByBlueprintNodeId))
                    .Where(delta => delta is not null)
                    .Cast<ProcessStepDelta>()
                    .ToList();

                if (nodeDeltas.Count > 0)
                {
                    batch.NodeUpdates.Add(new ProcessStepsUpdatedEvent
                    {
                        ProcessId = processId,
                        BlueprintDefinitionId = blueprintDefinitionId,
                        BlueprintVersionId = blueprintVersionId,
                        Nodes = nodeDeltas,
                    });
                }
            }
        }

        return batch.Summaries.Count == 0 && batch.NodeUpdates.Count == 0
            ? null
            : batch;
    }

    private async Task<BlueprintVersion?> LoadBlueprintGraphAsync(
        int blueprintVersionId,
        CancellationToken cancellationToken) =>
        await _dbContext.BlueprintVersions
            .Include(version => version.BlueprintNodes)
                .ThenInclude(blueprintNode => blueprintNode.TaskDefinition!)
                    .ThenInclude(taskDefinition => taskDefinition.Versions)
                        .ThenInclude(taskVersion => taskVersion.Channels)
            .Include(version => version.BlueprintNodes)
                .ThenInclude(blueprintNode => blueprintNode.OutgoingConnections)
            .Include(version => version.BlueprintNodes)
                .ThenInclude(blueprintNode => blueprintNode.IncomingConnections)
            .AsNoTracking()
            .AsSplitQuery()
            .FirstOrDefaultAsync(version => version.Id == blueprintVersionId, cancellationToken);

    private async Task<List<BlueprintNode>> LoadBlueprintNodesForDeltasAsync(
        int blueprintVersionId,
        IReadOnlyCollection<Guid> changedBlueprintNodeIds,
        CancellationToken cancellationToken)
    {
        if (changedBlueprintNodeIds.Count == 0)
        {
            return [];
        }

        return await _dbContext.BlueprintNodes
            .Where(blueprintNode =>
                blueprintNode.BlueprintVersionId == blueprintVersionId &&
                changedBlueprintNodeIds.Contains(blueprintNode.Id))
            .Include(blueprintNode => blueprintNode.TaskDefinition!)
                .ThenInclude(taskDefinition => taskDefinition.Versions)
                    .ThenInclude(taskVersion => taskVersion.Channels)
            .AsNoTracking()
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
    }

    private static ProcessDetailViewModel BuildDetail(
        BlueprintDefinition definition,
        BlueprintVersion blueprintVersionEntity,
        ProcessInstanceRecord<Dictionary<string, JsonElement>> processRecord,
        IReadOnlyCollection<ProcessStepRecord> processSteps)
    {
        var processStepsByBlueprintNodeId = GroupProcessStepsByBlueprintNodeId(processSteps);

        var nodes = blueprintVersionEntity.BlueprintNodes
            .OrderBy(blueprintNode => blueprintNode.PositionY)
            .ThenBy(blueprintNode => blueprintNode.PositionX)
            .Select(blueprintNode => BuildNodeViewModel(blueprintNode, processStepsByBlueprintNodeId))
            .ToList();

        return new ProcessDetailViewModel
        {
            ProcessId = Guid.Parse(processRecord.ProcessId),
            BlueprintDefinitionId = definition.Id,
            BlueprintName = definition.DisplayName,
            BlueprintVersionId = blueprintVersionEntity.Id,
            BlueprintVersionNumber = blueprintVersionEntity.Version,
            Status = NormalizeProcessStatus(processRecord.Status, processSteps),
            CreatedAt = processRecord.CreatedAt,
            UpdatedAt = processRecord.UpdatedAt,
            StartingBlueprintNodeId = blueprintVersionEntity.StartingBlueprintNodeId ?? Guid.Empty,
            Phase = processRecord.Phase is null
                ? null
                : new ProcessPhaseMetadataViewModel
                {
                    PhasedProcessId = Guid.Parse(processRecord.Phase.PhasedProcessId),
                    PhasedBlueprintDefinitionId = processRecord.Phase.PhasedBlueprintDefinitionId,
                    PhasedBlueprintVersionId = processRecord.Phase.PhasedBlueprintVersionId,
                    PhaseId = Guid.Parse(processRecord.Phase.PhaseId),
                    PhaseIndex = processRecord.Phase.PhaseIndex,
                    PhaseName = processRecord.Phase.PhaseName,
                },
            Nodes = nodes,
        };
    }

    private async Task<ProcessProjectionContext> LoadProcessContextAsync(
        int blueprintDefinitionId,
        Guid processId,
        CancellationToken cancellationToken)
    {
        if (_processIndexProjectionService is not null)
        {
            var directory = await _processIndexProjectionService.GetDirectoryByProcessIdAsync(
                processId,
                cancellationToken);

            if (directory is not null)
            {
                if (directory.BlueprintDefinitionId != blueprintDefinitionId)
                {
                    throw new EntityNotFoundException<ProcessDetailViewModel>(processId.ToString());
                }

                return await LoadProcessContextFromDirectoryAsync(directory, cancellationToken);
            }
        }

        var definition = await _dbContext.BlueprintDefinitions
            .Include(candidate => candidate.Versions)
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == blueprintDefinitionId, cancellationToken);

        if (definition is null)
        {
            throw new EntityNotFoundException<BlueprintViewModel>(blueprintDefinitionId);
        }

        ProcessInstanceRecord<Dictionary<string, JsonElement>>? processRecord = null;
        BlueprintVersion? blueprintVersion = null;

        foreach (var version in definition.Versions.OrderByDescending(candidate => candidate.Version))
        {
            try
            {
                processRecord = await _processInstanceRepository.GetProcessStateById(
                    processId,
                    version.Id,
                    cancellationToken);
                blueprintVersion = version;
                break;
            }
            catch (EntityNotFoundException<ProcessInstanceRecord<Dictionary<string, JsonElement>>>)
            {
                // Continue searching across blueprint versions.
            }
        }

        if (processRecord is null || blueprintVersion is null)
        {
            throw new EntityNotFoundException<ProcessDetailViewModel>(processId.ToString());
        }

        var blueprintVersionEntity = await LoadBlueprintGraphAsync(blueprintVersion.Id, cancellationToken)
            ?? throw new EntityNotFoundException<BlueprintVersionViewModel>(blueprintVersion.Id);
        var processSteps = await _processStepRepository.GetProcessStepsForProcess(
            processId,
            cancellationToken);
        var processStepsByBlueprintNodeId = GroupProcessStepsByBlueprintNodeId(processSteps);

        return new ProcessProjectionContext(
            definition,
            blueprintVersionEntity,
            processRecord,
            processSteps,
            processStepsByBlueprintNodeId);
    }

    private async Task<ProcessProjectionContext> LoadProcessContextByIdAsync(
        Guid processId,
        CancellationToken cancellationToken)
    {
        if (_processIndexProjectionService is null)
        {
            throw new EntityNotFoundException<ProcessDetailViewModel>(processId.ToString());
        }

        var directory = await _processIndexProjectionService.GetDirectoryByProcessIdAsync(
            processId,
            cancellationToken);

        if (directory is null)
        {
            throw new EntityNotFoundException<ProcessDetailViewModel>(processId.ToString());
        }

        return await LoadProcessContextFromDirectoryAsync(directory, cancellationToken);
    }

    private async Task<ProcessProjectionContext> LoadProcessContextFromDirectoryAsync(
        ProcessDirectoryRecord directory,
        CancellationToken cancellationToken)
    {
        var definition = await _dbContext.BlueprintDefinitions
            .Include(candidate => candidate.Versions)
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == directory.BlueprintDefinitionId, cancellationToken);

        if (definition is null)
        {
            throw new EntityNotFoundException<BlueprintViewModel>(directory.BlueprintDefinitionId);
        }

        var blueprintVersion = definition.Versions
            .FirstOrDefault(candidate => candidate.Id == directory.BlueprintVersionId)
            ?? throw new EntityNotFoundException<BlueprintVersionViewModel>(directory.BlueprintVersionId);

        var processRecord = await _processInstanceRepository.GetProcessStateById(
            Guid.Parse(directory.ProcessId),
            directory.BlueprintVersionId,
            cancellationToken);

        var blueprintVersionEntity = await LoadBlueprintGraphAsync(blueprintVersion.Id, cancellationToken)
            ?? throw new EntityNotFoundException<BlueprintVersionViewModel>(blueprintVersion.Id);
        var processSteps = await _processStepRepository.GetProcessStepsForProcess(
            Guid.Parse(directory.ProcessId),
            cancellationToken);
        var processStepsByBlueprintNodeId = GroupProcessStepsByBlueprintNodeId(processSteps);

        return new ProcessProjectionContext(
            definition,
            blueprintVersionEntity,
            processRecord,
            processSteps,
            processStepsByBlueprintNodeId);
    }

    private static JsonElement? SerializePayloads(IReadOnlyDictionary<string, JsonElement>? payloads)
    {
        if (payloads is null || payloads.Count == 0)
        {
            return null;
        }

        return JsonSerializer.SerializeToElement(payloads);
    }

    private static Dictionary<string, JsonElement> DeserializePayloads(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? [];
    }

    private static double? CalculateDurationMs(DateTimeOffset? startedAt, DateTimeOffset? endedAt)
    {
        if (!startedAt.HasValue || !endedAt.HasValue)
        {
            return null;
        }

        return Math.Max(0, (endedAt.Value - startedAt.Value).TotalMilliseconds);
    }

    private static ProcessSummaryViewModel BuildSummary(
        int blueprintDefinitionId,
        string blueprintName,
        BlueprintVersion blueprintVersion,
        ProcessInstanceRecord<Dictionary<string, JsonElement>> processRecord,
        IReadOnlyCollection<ProcessStepRecord> processSteps) =>
        BuildSummary(
            blueprintDefinitionId,
            blueprintName,
            blueprintVersion.Id,
            blueprintVersion.Version,
            processRecord,
            processSteps);

    private static ProcessSummaryViewModel BuildSummary(
        int blueprintDefinitionId,
        string blueprintName,
        int blueprintVersionId,
        int blueprintVersionNumber,
        ProcessInstanceRecord<Dictionary<string, JsonElement>> processRecord,
        IReadOnlyCollection<ProcessStepRecord> processSteps) =>
        new()
        {
            ProcessId = Guid.Parse(processRecord.ProcessId),
            BlueprintDefinitionId = blueprintDefinitionId,
            BlueprintName = blueprintName,
            BlueprintVersionId = blueprintVersionId,
            BlueprintVersionNumber = blueprintVersionNumber,
            Status = NormalizeProcessStatus(processRecord.Status, processSteps),
            CreatedAt = processRecord.CreatedAt,
            UpdatedAt = processRecord.UpdatedAt,
            CompletedTaskCount = processSteps.Count(task => task.Status == ProcessStepStatus.Completed),
            FailedTaskCount = processSteps.Count(task => task.Status == ProcessStepStatus.Failed),
            InProgressTaskCount = processSteps.Count(task => task.Status == ProcessStepStatus.InProgress),
            PendingTaskCount = processSteps.Count(task => task.Status == ProcessStepStatus.Pending),
        };

    private static ProcessSummaryUpdatedEvent BuildSummaryEvent(
        int blueprintDefinitionId,
        string blueprintName,
        int blueprintVersionId,
        int blueprintVersionNumber,
        ProcessInstanceRecord<Dictionary<string, JsonElement>> processRecord,
        IReadOnlyCollection<ProcessStepRecord> processSteps) =>
        new()
        {
            ProcessId = Guid.Parse(processRecord.ProcessId),
            BlueprintDefinitionId = blueprintDefinitionId,
            BlueprintName = blueprintName,
            BlueprintVersionId = blueprintVersionId,
            BlueprintVersionNumber = blueprintVersionNumber,
            Status = NormalizeProcessStatus(processRecord.Status, processSteps),
            CreatedAt = processRecord.CreatedAt,
            UpdatedAt = processRecord.UpdatedAt,
            CompletedTaskCount = processSteps.Count(task => task.Status == ProcessStepStatus.Completed),
            FailedTaskCount = processSteps.Count(task => task.Status == ProcessStepStatus.Failed),
            InProgressTaskCount = processSteps.Count(task => task.Status == ProcessStepStatus.InProgress),
            PendingTaskCount = processSteps.Count(task => task.Status == ProcessStepStatus.Pending),
        };

    private static ProcessStepViewModel BuildNodeViewModel(
        BlueprintNode blueprintNode,
        IReadOnlyDictionary<Guid, IReadOnlyList<ProcessStepRecord>> processStepsByBlueprintNodeId)
    {
        processStepsByBlueprintNodeId.TryGetValue(blueprintNode.Id, out var taskStates);
        var taskState = SelectRepresentativeStep(taskStates);
        var actualResolvedVersion = ResolveActualVersion(blueprintNode, taskState);

        return new ProcessStepViewModel
        {
            InstanceId = blueprintNode.Id,
            NodeKind = blueprintNode.NodeKind.ToString(),
            CollectionIteratorItemKind = blueprintNode.CollectionIteratorItemKind,
            RuntimeStepCount = taskStates?.Count ?? 0,
            TaskDefinitionId = blueprintNode.TaskDefinitionId ?? 0,
            AllowedMajor = blueprintNode.AllowedMajor,
            AllowedMinor = blueprintNode.AllowedMinor,
            ResolvedVersionId = taskState?.ResolvedTaskVersionId ?? actualResolvedVersion?.Id ?? 0,
            ResolvedVersion = taskState?.ResolvedTaskVersion ?? actualResolvedVersion?.Version.ToString() ?? "Native",
            Name = GetNodeDisplayName(blueprintNode),
            Status = MapAggregateStatus(taskStates),
            StartedAt = CalculateAggregateStartedAt(taskStates),
            CompletedAt = taskStates is not null && taskStates.Count > 0 && taskStates.All(step => step.Status == ProcessStepStatus.Completed)
                ? taskStates.Max(step => step.CompletedAt)
                : null,
            FailedAt = taskStates?.Where(step => step.FailedAt.HasValue).Max(step => step.FailedAt),
            FailureMessage = taskStates?.FirstOrDefault(step => step.Status == ProcessStepStatus.Failed)?.ErrorMessage,
            Position = new CoordinatesViewModel
            {
                X = blueprintNode.PositionX,
                Y = blueprintNode.PositionY,
            },
            InputChannelName = GetInputChannelName(blueprintNode, actualResolvedVersion),
            OutputChannelNames = GetOutputChannelNames(blueprintNode, actualResolvedVersion),
            OutgoingConnections = blueprintNode.OutgoingConnections
                .Where(connection => connection.DestinationBlueprintNodeId.HasValue)
                .Select(connection => new BlueprintConnectionViewModel
                {
                    ChannelId = actualResolvedVersion?.OutputChannels.FirstOrDefault(channel => channel.Name == connection.ChannelName)?.Id,
                    ChannelName = connection.ChannelName,
                    BlueprintNodeId = connection.DestinationBlueprintNodeId!.Value,
                    Anchor = connection.Anchor,
                    Side = connection.Side,
                    Position = connection.Position,
                    PropertyMappings = DeserializePropertyMappings(connection.PropertyMappingsJson),
                })
                .ToList(),
        };
    }

    private static ProcessStepDelta? BuildNodeDelta(
        BlueprintNode blueprintNode,
        IReadOnlyDictionary<Guid, IReadOnlyList<ProcessStepRecord>> processStepsByBlueprintNodeId)
    {
        if (!processStepsByBlueprintNodeId.TryGetValue(blueprintNode.Id, out var taskStates))
        {
            return null;
        }

        var taskState = SelectRepresentativeStep(taskStates);
        if (taskState is null)
        {
            return null;
        }

        var actualResolvedVersion = ResolveActualVersion(blueprintNode, taskState);

        return new ProcessStepDelta
        {
            InstanceId = blueprintNode.Id,
            BlueprintNodeId = Guid.Parse(taskState.Id),
            Status = MapAggregateStatus(taskStates),
            ResolvedVersionId = taskState.ResolvedTaskVersionId ?? actualResolvedVersion?.Id ?? 0,
            ResolvedVersion = taskState.ResolvedTaskVersion ?? actualResolvedVersion?.Version.ToString() ?? "Native",
            StartedAt = CalculateAggregateStartedAt(taskStates),
            CompletedAt = taskStates.Count > 0 && taskStates.All(step => step.Status == ProcessStepStatus.Completed)
                ? taskStates.Max(step => step.CompletedAt)
                : null,
            FailedAt = taskStates.Where(step => step.FailedAt.HasValue).Max(step => step.FailedAt),
            FailureMessage = taskStates.FirstOrDefault(step => step.Status == ProcessStepStatus.Failed)?.ErrorMessage,
            UpdatedAt = taskStates.Where(step => step.UpdatedAt.HasValue).Max(step => step.UpdatedAt),
        };
    }

    private static TaskVersion? ResolveActualVersion(BlueprintNode blueprintNode, ProcessStepRecord? taskState)
    {
        if (blueprintNode.NodeKind is BlueprintNodeKind.CollectionIterator)
        {
            return null;
        }

        var fallbackResolvedVersion = ResolveConcreteVersion(blueprintNode, requireActive: false);
        return taskState?.ResolvedTaskVersionId is int resolvedTaskVersionId
            ? blueprintNode.TaskDefinition?.Versions.FirstOrDefault(version => version.Id == resolvedTaskVersionId)
                ?? fallbackResolvedVersion
            : fallbackResolvedVersion;
    }

    private static string NormalizeProcessStatus(
        ProcessStatus processStatus,
        IReadOnlyCollection<ProcessStepRecord> tasks)
    {
        if (tasks.Any(task => task.Status == ProcessStepStatus.Failed))
        {
            return "Failed";
        }

        if (tasks.Any(task => task.Status == ProcessStepStatus.InProgress))
        {
            return "InProgress";
        }

        if (tasks.Any(task => task.Status == ProcessStepStatus.Pending))
        {
            return tasks.All(task => task.Status == ProcessStepStatus.Pending)
                ? "Pending"
                : "InProgress";
        }

        if (tasks.Count > 0 && tasks.All(task => task.Status == ProcessStepStatus.Completed))
        {
            return "Completed";
        }

        return processStatus.ToString();
    }

    private static IReadOnlyDictionary<Guid, IReadOnlyList<ProcessStepRecord>> GroupProcessStepsByBlueprintNodeId(
        IReadOnlyCollection<ProcessStepRecord> processSteps) =>
        processSteps
            .Where(task => Guid.TryParse(task.BlueprintNodeId, out var parsedId) && parsedId != Guid.Empty)
            .GroupBy(task => Guid.Parse(task.BlueprintNodeId))
            .ToDictionary(
                grouping => grouping.Key,
                grouping => (IReadOnlyList<ProcessStepRecord>)grouping
                    .OrderBy(step => step.IterationContext?.ItemIndex ?? -1)
                    .ThenBy(step => step.CreatedAt)
                    .ToList());

    private static ProcessStepRecord? SelectRepresentativeStep(
        IReadOnlyList<ProcessStepRecord>? taskStates)
    {
        if (taskStates is null || taskStates.Count == 0)
        {
            return null;
        }

        return taskStates.FirstOrDefault(step => step.Status == ProcessStepStatus.Failed)
            ?? taskStates.FirstOrDefault(step => step.Status == ProcessStepStatus.InProgress)
            ?? taskStates.FirstOrDefault(step => step.Status == ProcessStepStatus.Pending)
            ?? taskStates.FirstOrDefault(step => step.Status == ProcessStepStatus.Completed)
            ?? taskStates[0];
    }

    private static string MapAggregateStatus(IReadOnlyList<ProcessStepRecord>? taskStates)
    {
        if (taskStates is null || taskStates.Count == 0)
        {
            return "NotExecuted";
        }

        if (taskStates.Any(step => step.Status == ProcessStepStatus.Failed))
        {
            return "Failed";
        }

        if (taskStates.Any(step => step.Status == ProcessStepStatus.InProgress))
        {
            return "InProgress";
        }

        if (taskStates.Any(step => step.Status == ProcessStepStatus.Pending))
        {
            return taskStates.All(step => step.Status == ProcessStepStatus.Pending)
                ? "Pending"
                : "InProgress";
        }

        return taskStates.All(step => step.Status == ProcessStepStatus.Completed)
            ? "Completed"
            : "Pending";
    }

    private static DateTimeOffset? CalculateAggregateStartedAt(IReadOnlyList<ProcessStepRecord>? taskStates) =>
        taskStates?
            .Where(step => step.StartedAt.HasValue)
            .Min(step => step.StartedAt);

    private static DateTimeOffset? CalculateAggregateEndedAt(IReadOnlyList<ProcessStepRecord>? taskStates)
    {
        if (taskStates is null || taskStates.Count == 0)
        {
            return null;
        }

        return taskStates.Any(step => step.Status == ProcessStepStatus.Failed)
            ? taskStates.Where(step => step.FailedAt.HasValue).Max(step => step.FailedAt)
            : taskStates.All(step => step.Status == ProcessStepStatus.Completed)
                ? taskStates.Where(step => step.CompletedAt.HasValue).Max(step => step.CompletedAt)
                : null;
    }

    private static string GetNodeDisplayName(BlueprintNode blueprintNode) =>
        blueprintNode.NodeKind is BlueprintNodeKind.CollectionIterator
            ? "Collection Iterator"
            : blueprintNode.TaskDefinition?.DisplayName ?? blueprintNode.TaskDefinition?.Name ?? "Task";

    private static string? GetInputChannelName(BlueprintNode blueprintNode, TaskVersion? actualResolvedVersion) =>
        blueprintNode.NodeKind is BlueprintNodeKind.CollectionIterator
            ? "Collection"
            : actualResolvedVersion?.InputChannels.SingleOrDefault()?.Name;

    private static List<string> GetOutputChannelNames(BlueprintNode blueprintNode, TaskVersion? actualResolvedVersion) =>
        blueprintNode.NodeKind is BlueprintNodeKind.CollectionIterator
            ? ["Item"]
            : actualResolvedVersion?.OutputChannels
                .OrderBy(channel => channel.Id)
                .Select(channel => channel.Name)
                .ToList() ?? [];

    private static List<ProcessStepIterationViewModel> BuildIterationViewModels(
        IReadOnlyList<ProcessStepRecord>? taskStates) =>
        taskStates?
            .Where(step => step.IterationContext is not null)
            .OrderBy(step => step.IterationContext!.ItemIndex)
            .Select(step => new ProcessStepIterationViewModel
            {
                RuntimeStepId = Guid.TryParse(step.Id, out var runtimeStepId)
                    ? runtimeStepId
                    : Guid.Empty,
                SourceIteratorStepId = step.IterationContext!.SourceIteratorStepId,
                SourceIteratorBlueprintNodeId = step.IterationContext.SourceIteratorBlueprintNodeId,
                ItemIndex = step.IterationContext.ItemIndex,
                ItemKey = step.IterationContext.ItemKey,
                Status = MapTaskStatus(step.Status)
            })
            .ToList() ?? [];

    private static string MapTaskStatus(ProcessStepStatus status) =>
        status switch
        {
            ProcessStepStatus.Completed => "Completed",
            ProcessStepStatus.InProgress => "InProgress",
            ProcessStepStatus.Failed => "Failed",
            _ => "Pending",
        };

    private static TaskVersion ResolveConcreteVersion(BlueprintNode blueprintNode, bool requireActive)
    {
        if (blueprintNode.TaskDefinition is null)
        {
            throw new InvalidOperationException(
                $"Blueprint node {blueprintNode.Id} is not backed by a plugin task definition.");
        }

        var laneVersions = blueprintNode.TaskDefinition.Versions
            .Where(version => SemanticVersionUtility.IsSameLane(version.Version, blueprintNode.AllowedMajor, blueprintNode.AllowedMinor))
            .OrderByDescending(version => version.Version)
            .ToList();

        if (laneVersions.Count == 0)
        {
            throw new InvalidOperationException(
                $"No task version exists for task definition {blueprintNode.TaskDefinitionId} lane {blueprintNode.AllowedMajor}.{blueprintNode.AllowedMinor}.");
        }

        _ = requireActive;
        return laneVersions.First();
    }

    private sealed record ProcessProjectionContext(
        BlueprintDefinition Definition,
        BlueprintVersion BlueprintVersion,
        ProcessInstanceRecord<Dictionary<string, JsonElement>> ProcessRecord,
        IReadOnlyCollection<ProcessStepRecord> ProcessSteps,
        IReadOnlyDictionary<Guid, IReadOnlyList<ProcessStepRecord>> ProcessStepsByBlueprintNodeId);

    private static List<BlueprintPropertyMappingViewModel> DeserializePropertyMappings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<BlueprintPropertyMappingViewModel>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? [];
    }
}
