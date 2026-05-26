using Backend.Models;

namespace Backend.Engine;

public readonly record struct PluginRuntimeLane(
    int TaskDefinitionId,
    int Major,
    int Minor);

public sealed record PluginRuntimeResolution(
    int TaskVersionId,
    int TaskDefinitionId,
    string PluginName,
    Version Version);

public sealed class PluginRuntimeCatalog
{
    private readonly Lock _sync = new();
    private readonly Dictionary<int, TaskDefinitionInfo> _knownVersions = [];
    private readonly Dictionary<PluginRuntimeLane, int> _laneRoutes = [];

    public bool TryResolvePluginRuntime(
        int taskDefinitionId,
        int allowedMajor,
        int allowedMinor,
        out PluginRuntimeResolution resolution)
    {
        lock (_sync)
        {
            if (_laneRoutes.TryGetValue(
                    new PluginRuntimeLane(taskDefinitionId, allowedMajor, allowedMinor),
                    out var taskVersionId) &&
                _knownVersions.TryGetValue(taskVersionId, out var taskVersion))
            {
                resolution = new PluginRuntimeResolution(
                    taskVersion.Id,
                    taskVersion.TaskDefinitionId,
                    taskVersion.Name,
                    taskVersion.Version);
                return true;
            }
        }

        resolution = null!;
        return false;
    }

    public void ReplaceRoutes(
        IEnumerable<TaskDefinitionInfo> knownVersions,
        IReadOnlyDictionary<PluginRuntimeLane, TaskDefinitionInfo> routes)
    {
        lock (_sync)
        {
            _knownVersions.Clear();
            foreach (var version in knownVersions)
            {
                _knownVersions[version.Id] = version;
            }

            _laneRoutes.Clear();
            foreach (var (lane, version) in routes)
            {
                _laneRoutes[lane] = version.Id;
            }
        }
    }

    public void RemoveTaskVersionRoute(int taskVersionId)
    {
        lock (_sync)
        {
            foreach (var lane in _laneRoutes
                         .Where(route => route.Value == taskVersionId)
                         .Select(route => route.Key)
                         .ToArray())
            {
                _laneRoutes.Remove(lane);
            }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _knownVersions.Clear();
            _laneRoutes.Clear();
        }
    }
}
