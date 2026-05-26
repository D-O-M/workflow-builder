using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Models.ViewModels;
using Backend.Services.PluginRuntimeStatus;

namespace Backend.API.Services;

public sealed class TaskCatalogQueryService
{
    private readonly ITaskDefinitionRepository _taskDefinitionRepository;
    private readonly IPluginTaskVersionRuntimeStatusStore _runtimeStatusStore;
    private readonly PluginRuntimeStatusOptions _runtimeStatusOptions;

    public TaskCatalogQueryService(
        ITaskDefinitionRepository taskDefinitionRepository,
        IPluginTaskVersionRuntimeStatusStore runtimeStatusStore,
        PluginRuntimeStatusOptions runtimeStatusOptions)
    {
        _taskDefinitionRepository = taskDefinitionRepository;
        _runtimeStatusStore = runtimeStatusStore;
        _runtimeStatusOptions = runtimeStatusOptions;
    }

    public async Task<List<TaskDefinitionViewModel>> GetTaskCatalogAsync(CancellationToken cancellationToken)
    {
        var taskDefinitions = await _taskDefinitionRepository.GetAllTaskDefinitionViewModels(cancellationToken);
        var taskVersionIds = taskDefinitions
            .SelectMany(definition => definition.Versions)
            .Select(version => version.Id)
            .Distinct()
            .ToArray();
        if (taskVersionIds.Length == 0)
        {
            return taskDefinitions;
        }

        var freshAfter = DateTimeOffset.UtcNow.Subtract(_runtimeStatusOptions.StaleAfter);
        var runtimeRecords = await _runtimeStatusStore.GetFreshByTaskVersionIdsAsync(
            taskVersionIds,
            freshAfter,
            cancellationToken);
        var recordsByTaskVersionId = runtimeRecords
            .Where(record => record.LastSeenAt >= freshAfter)
            .GroupBy(record => record.TaskVersionId)
            .ToDictionary(group => group.Key, group => group.ToArray());

        return
        [
            .. taskDefinitions.Select(definition => definition with
            {
                Versions =
                [
                    .. definition.Versions.Select(version => version with
                    {
                        RuntimeAvailability = Aggregate(
                            recordsByTaskVersionId.TryGetValue(version.Id, out var records)
                                ? records
                                : [])
                    })
                ]
            })
        ];
    }

    private static TaskVersionRuntimeAvailabilityViewModel Aggregate(
        IReadOnlyList<PluginTaskVersionRuntimeStatusRecord> records)
    {
        if (records.Count == 0)
        {
            return new TaskVersionRuntimeAvailabilityViewModel
            {
                Status = PluginTaskVersionRuntimeStatus.UnknownNotLoaded.ToString(),
                Runnable = false,
                ReportingEngineCount = 0
            };
        }

        var selected = records
            .OrderByDescending(record => StatusPriority(record.CurrentStatus))
            .ThenByDescending(record => record.UpdatedAt)
            .First();
        var reportingEngineCount = records
            .Select(record => record.EngineInstanceId)
            .Distinct(StringComparer.Ordinal)
            .Count();

        return new TaskVersionRuntimeAvailabilityViewModel
        {
            Status = selected.CurrentStatus.ToString(),
            Runnable = selected.CurrentStatus == PluginTaskVersionRuntimeStatus.Runnable,
            ReportingEngineCount = reportingEngineCount,
            LastUpdatedAt = records.Max(record => record.UpdatedAt),
            Message = PluginIngestionStatusMessageSanitizer.Sanitize(selected.Message),
            Issues =
            [
                .. selected.Issues
                    .Take(5)
                    .Select(issue => new TaskVersionRuntimeIssueViewModel
                    {
                        CapabilityId = issue.CapabilityId,
                        DisplayName = issue.DisplayName,
                        Kind = issue.Kind,
                        Status = issue.Status,
                        Category = issue.Category,
                        Message = PluginIngestionStatusMessageSanitizer.Sanitize(issue.Message),
                        Remediation = PluginIngestionStatusMessageSanitizer.Sanitize(issue.Remediation)
                    })
            ]
        };
    }

    private static int StatusPriority(PluginTaskVersionRuntimeStatus status) =>
        status switch
        {
            PluginTaskVersionRuntimeStatus.Runnable => 100,
            PluginTaskVersionRuntimeStatus.RuntimeLoadFailed => 80,
            PluginTaskVersionRuntimeStatus.Quarantined => 75,
            PluginTaskVersionRuntimeStatus.UnrunnableAbiIncompatible => 70,
            PluginTaskVersionRuntimeStatus.UnrunnableMissingConfiguration => 60,
            PluginTaskVersionRuntimeStatus.UnrunnableMissingCapability => 50,
            PluginTaskVersionRuntimeStatus.UnknownNotLoaded => 20,
            PluginTaskVersionRuntimeStatus.Registered => 10,
            _ => 0
        };
}
