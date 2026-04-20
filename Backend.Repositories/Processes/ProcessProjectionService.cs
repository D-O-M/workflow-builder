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

    public ProcessProjectionService(
        BlueprintDbContext dbContext,
        IProcessStepRepository processStepRepository,
        IProcessInstanceRepository<Dictionary<string, JsonElement>> processInstanceRepository)
    {
        _dbContext = dbContext;
        _processStepRepository = processStepRepository;
        _processInstanceRepository = processInstanceRepository;
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
        context.ProcessStepsByBlueprintNodeId.TryGetValue(blueprintNode.Id, out var taskState);
        var actualResolvedVersion = ResolveActualVersion(blueprintNode, taskState);
        var endedAt = taskState?.FailedAt ?? taskState?.CompletedAt;

        return new ProcessStepDetailViewModel
        {
            BlueprintNodeId = blueprintNode.Id,
            Name = blueprintNode.TaskDefinition.Name,
            Status = taskState is not null
                ? MapTaskStatus(taskState.Status)
                : "NotExecuted",
            ResolvedVersion = taskState?.ResolvedTaskVersion ?? actualResolvedVersion.Version.ToString(),
            StartedAt = taskState?.StartedAt,
            EndedAt = endedAt,
            DurationMs = CalculateDurationMs(taskState?.StartedAt, endedAt),
            FailureMessage = taskState?.ErrorMessage,
            Inputs = ResolveStepInputs(blueprintNode, actualResolvedVersion, context),
            Outputs = SerializePayloads(taskState?.OutputData),
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

        if (!context.ProcessStepsByBlueprintNodeId.TryGetValue(blueprintNode.Id, out var taskState))
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
                var processStepsByBlueprintNodeId = processSteps
                    .Where(task => Guid.TryParse(task.BlueprintNodeId, out var parsedId) && parsedId != Guid.Empty)
                    .ToDictionary(task => Guid.Parse(task.BlueprintNodeId), task => task);

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
                .ThenInclude(blueprintNode => blueprintNode.TaskDefinition)
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
            .Include(blueprintNode => blueprintNode.TaskDefinition)
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
        var processStepsByBlueprintNodeId = processSteps
            .Where(task => Guid.TryParse(task.BlueprintNodeId, out var parsedId) && parsedId != Guid.Empty)
            .ToDictionary(task => Guid.Parse(task.BlueprintNodeId), task => task);

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
            Nodes = nodes,
        };
    }

    private async Task<ProcessProjectionContext> LoadProcessContextAsync(
        int blueprintDefinitionId,
        Guid processId,
        CancellationToken cancellationToken)
    {
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
        var processStepsByBlueprintNodeId = processSteps
            .Where(task => Guid.TryParse(task.BlueprintNodeId, out var parsedId) && parsedId != Guid.Empty)
            .ToDictionary(task => Guid.Parse(task.BlueprintNodeId), task => task);

        return new ProcessProjectionContext(
            definition,
            blueprintVersionEntity,
            processRecord,
            processSteps,
            processStepsByBlueprintNodeId);
    }

    private static JsonElement? ResolveStepInputs(
        BlueprintNode blueprintNode,
        TaskVersion resolvedVersion,
        ProcessProjectionContext context)
    {
        var inputChannelName = resolvedVersion.InputChannels.SingleOrDefault()?.Name;
        var incomingConnection = blueprintNode.IncomingConnections
            .FirstOrDefault(connection => connection.SourceBlueprintNodeId.HasValue);

        if (incomingConnection is not null)
        {
            if (!context.ProcessStepsByBlueprintNodeId.TryGetValue(incomingConnection.SourceBlueprintNodeId!.Value, out var sourceTaskState))
            {
                return null;
            }

            return TryBuildNamedPayload(
                sourceTaskState.OutputData,
                incomingConnection.ChannelName,
                inputChannelName ?? incomingConnection.ChannelName);
        }

        var seedTask = context.ProcessSteps
            .FirstOrDefault(task => string.Equals(task.BlueprintNodeId, Guid.Empty.ToString(), StringComparison.OrdinalIgnoreCase));

        return seedTask is null || string.IsNullOrWhiteSpace(inputChannelName)
            ? null
            : TryBuildNamedPayload(seedTask.OutputData, inputChannelName, inputChannelName);
    }

    private static JsonElement? TryBuildNamedPayload(
        IReadOnlyDictionary<string, JsonElement>? payloads,
        string payloadKey,
        string propertyName)
    {
        if (payloads is null || !payloads.TryGetValue(payloadKey, out var payload))
        {
            return null;
        }

        return JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
        {
            [propertyName] = payload,
        });
    }

    private static JsonElement? SerializePayloads(IReadOnlyDictionary<string, JsonElement>? payloads)
    {
        if (payloads is null || payloads.Count == 0)
        {
            return null;
        }

        return JsonSerializer.SerializeToElement(payloads);
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
        IReadOnlyDictionary<Guid, ProcessStepRecord> processStepsByBlueprintNodeId)
    {
        processStepsByBlueprintNodeId.TryGetValue(blueprintNode.Id, out var taskState);
        var actualResolvedVersion = ResolveActualVersion(blueprintNode, taskState);

        return new ProcessStepViewModel
        {
            InstanceId = blueprintNode.Id,
            TaskDefinitionId = blueprintNode.TaskDefinitionId,
            AllowedMajor = blueprintNode.AllowedMajor,
            AllowedMinor = blueprintNode.AllowedMinor,
            ResolvedVersionId = taskState?.ResolvedTaskVersionId ?? actualResolvedVersion.Id,
            ResolvedVersion = taskState?.ResolvedTaskVersion ?? actualResolvedVersion.Version.ToString(),
            Name = blueprintNode.TaskDefinition.Name,
            Status = taskState is not null
                ? MapTaskStatus(taskState.Status)
                : "NotExecuted",
            StartedAt = taskState?.StartedAt,
            CompletedAt = taskState?.CompletedAt,
            FailedAt = taskState?.FailedAt,
            FailureMessage = taskState?.ErrorMessage,
            Position = new CoordinatesViewModel
            {
                X = blueprintNode.PositionX,
                Y = blueprintNode.PositionY,
            },
            InputChannelName = actualResolvedVersion.InputChannels.SingleOrDefault()?.Name,
            OutputChannelNames = actualResolvedVersion.OutputChannels
                .OrderBy(channel => channel.Id)
                .Select(channel => channel.Name)
                .ToList(),
            OutgoingConnections = blueprintNode.OutgoingConnections
                .Where(connection => connection.DestinationBlueprintNodeId.HasValue)
                .Select(connection => new BlueprintConnectionViewModel
                {
                    ChannelId = actualResolvedVersion.OutputChannels.FirstOrDefault(channel => channel.Name == connection.ChannelName)?.Id,
                    ChannelName = connection.ChannelName,
                    BlueprintNodeId = connection.DestinationBlueprintNodeId!.Value,
                    Anchor = connection.Anchor,
                    Side = connection.Side,
                    Position = connection.Position,
                })
                .ToList(),
        };
    }

    private static ProcessStepDelta? BuildNodeDelta(
        BlueprintNode blueprintNode,
        IReadOnlyDictionary<Guid, ProcessStepRecord> processStepsByBlueprintNodeId)
    {
        if (!processStepsByBlueprintNodeId.TryGetValue(blueprintNode.Id, out var taskState))
        {
            return null;
        }

        var actualResolvedVersion = ResolveActualVersion(blueprintNode, taskState);

        return new ProcessStepDelta
        {
            InstanceId = blueprintNode.Id,
            BlueprintNodeId = Guid.Parse(taskState.Id),
            Status = MapTaskStatus(taskState.Status),
            ResolvedVersionId = taskState.ResolvedTaskVersionId ?? actualResolvedVersion.Id,
            ResolvedVersion = taskState.ResolvedTaskVersion ?? actualResolvedVersion.Version.ToString(),
            StartedAt = taskState.StartedAt,
            CompletedAt = taskState.CompletedAt,
            FailedAt = taskState.FailedAt,
            FailureMessage = taskState.ErrorMessage,
            UpdatedAt = taskState.UpdatedAt,
        };
    }

    private static TaskVersion ResolveActualVersion(BlueprintNode blueprintNode, ProcessStepRecord? taskState)
    {
        var fallbackResolvedVersion = ResolveConcreteVersion(blueprintNode, requireActive: false);
        return taskState?.ResolvedTaskVersionId is int resolvedTaskVersionId
            ? blueprintNode.TaskDefinition.Versions.FirstOrDefault(version => version.Id == resolvedTaskVersionId)
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
        IReadOnlyDictionary<Guid, ProcessStepRecord> ProcessStepsByBlueprintNodeId);
}
