using Backend.Models.Realtime;

namespace Backend.SignalR.Services;

public sealed class ExecutionSubscriptionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<string>> _groupsByConnection = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _connectionsByGroup = new(StringComparer.Ordinal);

    public void Subscribe(string connectionId, string groupName)
    {
        lock (_gate)
        {
            if (!_groupsByConnection.TryGetValue(connectionId, out var groups))
            {
                groups = new HashSet<string>(StringComparer.Ordinal);
                _groupsByConnection[connectionId] = groups;
            }

            if (!groups.Add(groupName))
            {
                return;
            }

            if (!_connectionsByGroup.TryGetValue(groupName, out var connections))
            {
                connections = new HashSet<string>(StringComparer.Ordinal);
                _connectionsByGroup[groupName] = connections;
            }

            connections.Add(connectionId);
        }
    }

    public void Unsubscribe(string connectionId, string groupName)
    {
        lock (_gate)
        {
            if (_groupsByConnection.TryGetValue(connectionId, out var groups))
            {
                groups.Remove(groupName);
                if (groups.Count == 0)
                {
                    _groupsByConnection.Remove(connectionId);
                }
            }

            if (_connectionsByGroup.TryGetValue(groupName, out var connections))
            {
                connections.Remove(connectionId);
                if (connections.Count == 0)
                {
                    _connectionsByGroup.Remove(groupName);
                }
            }
        }
    }

    public void RemoveConnection(string connectionId)
    {
        lock (_gate)
        {
            if (!_groupsByConnection.Remove(connectionId, out var groups))
            {
                return;
            }

            foreach (var groupName in groups)
            {
                if (!_connectionsByGroup.TryGetValue(groupName, out var connections))
                {
                    continue;
                }

                connections.Remove(connectionId);
                if (connections.Count == 0)
                {
                    _connectionsByGroup.Remove(groupName);
                }
            }
        }
    }

    public bool HasBlueprintSubscribers(int blueprintDefinitionId) =>
        HasSubscribers(ExecutionHubGroups.Blueprint(blueprintDefinitionId));

    public bool HasWorkflowSubscribers(Guid workflowInstanceId) =>
        HasSubscribers(ExecutionHubGroups.Workflow(workflowInstanceId));

    public ExecutionSubscriptionSnapshotViewModel GetSnapshot()
    {
        lock (_gate)
        {
            return new ExecutionSubscriptionSnapshotViewModel
            {
                BlueprintDefinitionIds = _connectionsByGroup.Keys
                    .Where(groupName => ExecutionHubGroups.TryParseBlueprint(groupName, out _))
                    .Select(groupName =>
                    {
                        ExecutionHubGroups.TryParseBlueprint(groupName, out var blueprintDefinitionId);
                        return blueprintDefinitionId;
                    })
                    .OrderBy(id => id)
                    .ToList(),
                WorkflowInstanceIds = _connectionsByGroup.Keys
                    .Where(groupName => ExecutionHubGroups.TryParseWorkflow(groupName, out _))
                    .Select(groupName =>
                    {
                        ExecutionHubGroups.TryParseWorkflow(groupName, out var workflowInstanceId);
                        return workflowInstanceId;
                    })
                    .OrderBy(id => id)
                    .ToList(),
            };
        }
    }

    private bool HasSubscribers(string groupName)
    {
        lock (_gate)
        {
            return _connectionsByGroup.TryGetValue(groupName, out var connections) &&
                connections.Count > 0;
        }
    }
}
