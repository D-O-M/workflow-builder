using Backend.Database.State.Objects;
using Backend.Engine.Capabilities;
using Backend.Interfaces.Repositories;
using Backend.Models;

namespace Backend.Engine;

public sealed class PluginTaskVersionRuntimeStatusPublisher
{
    private readonly IPluginTaskVersionRuntimeStatusStore _statusStore;
    private readonly EngineInstanceInfo _engineInstanceInfo;
    private readonly ILogger<PluginTaskVersionRuntimeStatusPublisher> _logger;
    private readonly Lock _sync = new();
    private readonly HashSet<int> _knownTaskVersionIds = [];

    public PluginTaskVersionRuntimeStatusPublisher(
        IPluginTaskVersionRuntimeStatusStore statusStore,
        EngineInstanceInfo engineInstanceInfo,
        ILogger<PluginTaskVersionRuntimeStatusPublisher> logger)
    {
        _statusStore = statusStore;
        _engineInstanceInfo = engineInstanceInfo;
        _logger = logger;
    }

    public async Task PublishAsync(
        TaskDefinitionInfo taskInfo,
        PluginTaskVersionRuntimeStatus status,
        string? message,
        IReadOnlyCollection<CapabilityValidationResult>? issues,
        CancellationToken cancellationToken)
    {
        try
        {
            await _statusStore.UpsertAsync(
                new PluginTaskVersionRuntimeStatusUpdate
                {
                    TaskVersionId = taskInfo.Id,
                    TaskDefinitionId = taskInfo.TaskDefinitionId,
                    PluginName = taskInfo.Name,
                    PluginVersion = taskInfo.Version,
                    EngineInstanceId = _engineInstanceInfo.InstanceId.ToString("D"),
                    Status = status,
                    Message = message,
                    Issues = issues is null ? [] : [.. issues.Select(MapIssue)]
                },
                cancellationToken);

            lock (_sync)
            {
                _knownTaskVersionIds.Add(taskInfo.Id);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Unable to publish plugin runtime status {Status} for task version {TaskVersionId}.",
                status,
                taskInfo.Id);
        }
    }

    public async Task RefreshKnownObservationsAsync(CancellationToken cancellationToken)
    {
        int[] knownTaskVersionIds;
        lock (_sync)
        {
            knownTaskVersionIds = [.. _knownTaskVersionIds];
        }

        if (knownTaskVersionIds.Length == 0)
        {
            return;
        }

        try
        {
            await _statusStore.RefreshLastSeenAsync(
                _engineInstanceInfo.InstanceId.ToString("D"),
                knownTaskVersionIds,
                DateTimeOffset.UtcNow,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Unable to refresh plugin runtime status heartbeat.");
        }
    }

    private static PluginTaskVersionRuntimeIssueRecord MapIssue(CapabilityValidationResult result) =>
        new()
        {
            CapabilityId = result.CapabilityId,
            DisplayName = result.DisplayName,
            Kind = result.Kind,
            Status = result.Status.ToString(),
            Category = result.FailureCategory.ToString(),
            Message = result.Message,
            Remediation = result.Remediation
        };
}
