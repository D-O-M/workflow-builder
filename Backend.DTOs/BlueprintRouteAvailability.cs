using Backend.Database.Relational.Entities;
using Backend.Models.InputModels;

namespace Backend.Models;

/// <summary>Symbolic reachability for a DAG. A branch introduces one Boolean variable.
/// Reduced decision diagrams let both arms of a diamond simplify back to the parent's route.</summary>
public sealed class BlueprintRouteAvailability
{
    private sealed record Node(Guid Id, BlueprintNodeKind Kind, List<(Guid Target, string Channel)> Edges);
    private readonly record struct Decision(int Variable, int Low, int High);
    private const int Never = 0;
    public const int Always = 1;
    private readonly Dictionary<Guid, int> _reachable = [];
    private readonly Dictionary<Guid, int> _branches = [];
    private readonly List<Decision> _decisions = [new(int.MaxValue, 0, 0), new(int.MaxValue, 1, 1)];
    private readonly Dictionary<Decision, int> _unique = [];
    private readonly Dictionary<(bool Conjunction, int Left, int Right), int> _cached = [];

    public static BlueprintRouteAvailability From(IEnumerable<BlueprintNodeInputModel> nodes) =>
        new(nodes.Select(n => new Node(n.InstanceId, Enum.TryParse<BlueprintNodeKind>(n.NodeKind, true, out var kind) ? kind : BlueprintNodeKind.PluginTask, n.OutgoingConnections
            .Select(c => (c.ConnectedBlueprintNodeId, c.ChannelName ?? "")).ToList())).ToList());

    public static BlueprintRouteAvailability From(IEnumerable<BlueprintNode> nodes) =>
        new(nodes.Select(n => new Node(n.Id, n.NodeKind, n.OutgoingConnections
            .Where(c => c.DestinationBlueprintNodeId.HasValue)
            .Select(c => (c.DestinationBlueprintNodeId!.Value, c.ChannelName)).ToList())).ToList());

    private BlueprintRouteAvailability(List<Node> nodes)
    {
        var byId = nodes.ToDictionary(n => n.Id);
        var indegree = nodes.ToDictionary(n => n.Id, _ => 0);
        foreach (var node in nodes)
            foreach (var edge in node.Edges)
                if (byId.ContainsKey(edge.Target)) indegree[edge.Target]++;

        var ready = new Queue<Node>(nodes.Where(n => indegree[n.Id] == 0));
        var scope = new Dictionary<Guid, Guid>();
        foreach (var root in ready)
            _reachable[root.Id] = Always;

        var nextVariable = 0;
        while (ready.TryDequeue(out var node))
        {
            if (node.Kind == BlueprintNodeKind.ConditionalBranch)
                _branches[node.Id] = nextVariable++;
            var childScope = node.Kind == BlueprintNodeKind.CollectionIterator
                ? node.Id : scope.GetValueOrDefault(node.Id);

            foreach (var (target, channel) in node.Edges.Where(e => byId.ContainsKey(e.Target)))
            {
                if (scope.TryGetValue(target, out var existing) && existing != childScope)
                    throw new InvalidDataException("Connections that rejoin must share a Collection Iterator item scope.");
                scope[target] = childScope;
                _reachable[target] = Union(_reachable.GetValueOrDefault(target), Output(node.Id, channel));
                if (--indegree[target] == 0) ready.Enqueue(byId[target]);
            }
        }
    }

    public int NodeRoute(Guid id) => _reachable.GetValueOrDefault(id);
    public int Output(Guid id, string channel)
    {
        var route = NodeRoute(id);
        if (!_branches.TryGetValue(id, out var variable)) return route;
        return channel switch
        {
            "True" => Apply(true, route, Make(variable, Never, Always)),
            "False" => Apply(true, route, Make(variable, Always, Never)),
            _ => Never
        };
    }
    public bool Overlap(int left, int right) => Apply(true, left, right) != Never;
    public int Union(int left, int right) => Apply(false, left, right);

    private int Apply(bool and, int left, int right)
    {
        if (left == right) return left;
        if (left > right) (left, right) = (right, left);
        if (left == Never) return and ? Never : right;
        if (left == Always) return and ? right : Always;
        var key = (and, left, right);
        if (_cached.TryGetValue(key, out var answer)) return answer;
        var a = _decisions[left];
        var b = _decisions[right];
        var variable = Math.Min(a.Variable, b.Variable);
        var low = Apply(and, a.Variable == variable ? a.Low : left, b.Variable == variable ? b.Low : right);
        var high = Apply(and, a.Variable == variable ? a.High : left, b.Variable == variable ? b.High : right);
        return _cached[key] = Make(variable, low, high);
    }

    private int Make(int variable, int low, int high)
    {
        if (low == high) return low;
        var decision = new Decision(variable, low, high);
        if (_unique.TryGetValue(decision, out var id)) return id;
        if (_decisions.Count >= 65536)
            throw new InvalidDataException("Conditional routing is too complex to validate. Split the Blueprint into smaller phases.");
        _unique.Add(decision, _decisions.Count);
        _decisions.Add(decision);
        return _decisions.Count - 1;
    }
}
