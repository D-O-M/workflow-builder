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
    private HashSet<PluginRuntimeLane> _disabledLanes = [];
    private long _policyRevision;
    private HashSet<PluginRuntimeLane> _configDrains = [];

    public IReadOnlyCollection<int> GetRoutedVersionIds()
    {
        lock (_sync)
        {
            return _laneRoutes.Values.ToArray();
        }
    }

    public void BeginConfigurationDrain(PluginRuntimeLane lane, long revision)
    {
        lock (_sync)
        {
            _configDrains.Add(lane);
            _policyRevision = revision;
        }
    }

    public bool TryGetDrainingLane(int taskDefinitionId, int major, int minor, out long revision)
    {
        lock (_sync)
        {
            revision = _policyRevision;
            return _configDrains.Contains(new(taskDefinitionId, major, minor));
        }
    }

    public bool TryGetDisabledLane(int taskDefinitionId, int major, int minor, out long policyRevision)
    {
        lock (_sync)
        {
            policyRevision = _policyRevision;
            return _disabledLanes.Contains(new PluginRuntimeLane(taskDefinitionId, major, minor));
        }
    }

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
        IReadOnlyDictionary<PluginRuntimeLane, TaskDefinitionInfo> routes,
        HashSet<PluginRuntimeLane>? disabledLanes = null,
        long policyRevision = 0,
        IReadOnlySet<PluginRuntimeLane>? drainingLanes = null)
    {
        lock (_sync)
        {
            _knownVersions.Clear();
            foreach (var version in knownVersions)
            {
                _knownVersions[version.Id] = version;
            }

            _laneRoutes.Clear();
            _disabledLanes = disabledLanes ?? [];
            _policyRevision = policyRevision;
            _configDrains = drainingLanes?.Where(lane => !routes.ContainsKey(lane)).ToHashSet() ?? [];
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
            _disabledLanes.Clear();
            _configDrains.Clear();
        }
    }
}
