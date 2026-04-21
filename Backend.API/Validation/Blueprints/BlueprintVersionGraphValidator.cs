using Backend.Models.InputModels;

namespace Backend.API.Validation.Blueprints;

public sealed class BlueprintVersionGraphValidator : IBlueprintVersionValidator
{
    public Task<BlueprintValidationResult> ValidateAsync(BlueprintVersionInputModel model, CancellationToken ct = default)
    {
        var result = new BlueprintValidationResult();

        if (model.Tasks.Count == 0)
        {
            result.Add("graph.empty", "Tasks must not be empty.", "tasks");
            return Task.FromResult(result);
        }

        var tasksByInstanceId = new Dictionary<Guid, BlueprintNodeInputModel>();
        for (var i = 0; i < model.Tasks.Count; i++)
        {
            var t = model.Tasks[i];
            if (t.InstanceId == Guid.Empty)
            {
                result.Add("task.instanceId.empty", "Task instanceId must be a non-empty GUID.", $"tasks[{i}].instanceId");
                continue;
            }

            if (!tasksByInstanceId.TryAdd(t.InstanceId, t))
                result.Add("task.instanceId.duplicate", "Task instanceId must be unique.", $"tasks[{i}].instanceId");
        }

        if (model.StartingBlueprintNodeId == Guid.Empty)
            result.Add("graph.start.empty", "startingBlueprintNodeId must be a non-empty GUID.", "startingBlueprintNodeId");
        else if (!tasksByInstanceId.ContainsKey(model.StartingBlueprintNodeId))
            result.Add("graph.start.missing", "startingBlueprintNodeId must reference a task in tasks[].", "startingBlueprintNodeId");

        if (!result.IsValid)
            return Task.FromResult(result);
        var orderedTaskIds = model.Tasks.Select(task => task.InstanceId).ToList();
        var nodeIndex = orderedTaskIds.Select((id, idx) => (id, idx)).ToDictionary(x => x.id, x => x.idx);

        // Detect cycles (must be a DAG) and ensure all nodes are reachable from the starting node.
        var visited = new HashSet<Guid>();
        var stack = new HashSet<Guid>();
        var reachable = new HashSet<Guid>();

        bool Dfs(Guid id)
        {
            reachable.Add(id);

            if (stack.Contains(id))
                return true;

            if (visited.Contains(id))
                return false;

            visited.Add(id);
            stack.Add(id);

            var task = tasksByInstanceId[id];
            foreach (var c in task.OutgoingConnections)
            {
                if (c.ConnectedBlueprintNodeId == Guid.Empty)
                    continue;

                if (!tasksByInstanceId.ContainsKey(c.ConnectedBlueprintNodeId))
                {
                    result.Add(
                        "graph.connection.missingTarget",
                        "Connection target must reference a task in tasks[].",
                        $"tasks[{nodeIndex[id]}].outgoingConnections");
                    continue;
                }

                if (Dfs(c.ConnectedBlueprintNodeId))
                    return true;
            }

            stack.Remove(id);
            return false;
        }

        if (Dfs(model.StartingBlueprintNodeId))
            result.Add("graph.cycle", "Task graph must be acyclic (no cycles).", "tasks");

        foreach (var task in model.Tasks)
        {
            if (!reachable.Contains(task.InstanceId))
                result.Add("graph.unreachable", "All tasks must be reachable from startingBlueprintNodeId.", $"tasks[{nodeIndex[task.InstanceId]}]");
        }

        var incomingCounts = new Dictionary<Guid, int>();
        foreach (var task in model.Tasks)
        {
            foreach (var conn in task.OutgoingConnections)
            {
                if (!tasksByInstanceId.ContainsKey(conn.ConnectedBlueprintNodeId))
                    continue;

                incomingCounts.TryGetValue(conn.ConnectedBlueprintNodeId, out var count);
                incomingCounts[conn.ConnectedBlueprintNodeId] = count + 1;
            }
        }

        foreach (var (destId, count) in incomingCounts)
        {
            if (destId == model.StartingBlueprintNodeId)
            {
                if (count > 0)
                    result.Add("graph.start.incoming", "Starting task must not have any incoming connections.", "startingBlueprintNodeId");
            }
        }

        if (!result.IsValid)
            return Task.FromResult(result);

        var topologicalOrder = BuildTopologicalOrder(model.Tasks, tasksByInstanceId);
        foreach (var task in model.Tasks)
        {
            foreach (var conn in task.OutgoingConnections)
            {
                if (!topologicalOrder.TryGetValue(task.InstanceId, out var sourceOrder) ||
                    !topologicalOrder.TryGetValue(conn.ConnectedBlueprintNodeId, out var destinationOrder))
                {
                    continue;
                }

                if (destinationOrder <= sourceOrder)
                {
                    result.Add(
                        "graph.executionOrder.backward",
                        "Connections must always point forward in execution order.",
                        $"tasks[{nodeIndex[task.InstanceId]}].outgoingConnections");
                }
            }
        }

        return Task.FromResult(result);
    }

    private static Dictionary<Guid, int> BuildTopologicalOrder(
        IReadOnlyCollection<BlueprintNodeInputModel> tasks,
        IReadOnlyDictionary<Guid, BlueprintNodeInputModel> tasksByInstanceId)
    {
        var visited = new HashSet<Guid>();
        var order = new List<Guid>(tasks.Count);

        void Visit(Guid nodeId)
        {
            if (!visited.Add(nodeId))
            {
                return;
            }

            foreach (var targetId in tasksByInstanceId[nodeId].OutgoingConnections
                         .Select(connection => connection.ConnectedBlueprintNodeId)
                         .Where(tasksByInstanceId.ContainsKey)
                         .OrderBy(id => id))
            {
                Visit(targetId);
            }

            order.Add(nodeId);
        }

        foreach (var nodeId in tasks.Select(task => task.InstanceId).OrderBy(id => id))
        {
            Visit(nodeId);
        }

        order.Reverse();
        return order
            .Select((nodeId, index) => new { nodeId, index })
            .ToDictionary(item => item.nodeId, item => item.index);
    }
}
