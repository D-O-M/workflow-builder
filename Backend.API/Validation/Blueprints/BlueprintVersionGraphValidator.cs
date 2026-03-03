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

        var tasksByInstanceId = new Dictionary<Guid, BlueprintTaskInputModel>();
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

        if (model.StartingTaskInstanceId == Guid.Empty)
            result.Add("graph.start.empty", "startingTaskInstanceId must be a non-empty GUID.", "startingTaskInstanceId");
        else if (!tasksByInstanceId.ContainsKey(model.StartingTaskInstanceId))
            result.Add("graph.start.missing", "startingTaskInstanceId must reference a task in tasks[].", "startingTaskInstanceId");

        if (!result.IsValid)
            return Task.FromResult(result);

        var nodeIndex = tasksByInstanceId.Keys.Select((id, idx) => (id, idx)).ToDictionary(x => x.id, x => x.idx);

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
                if (c.ConnectedTaskInstanceId == Guid.Empty)
                    continue;

                if (!tasksByInstanceId.ContainsKey(c.ConnectedTaskInstanceId))
                {
                    result.Add(
                        "graph.connection.missingTarget",
                        "Connection target must reference a task in tasks[].",
                        $"tasks[{nodeIndex[id]}].outgoingConnections");
                    continue;
                }

                if (Dfs(c.ConnectedTaskInstanceId))
                    return true;
            }

            stack.Remove(id);
            return false;
        }

        if (Dfs(model.StartingTaskInstanceId))
            result.Add("graph.cycle", "Task graph must be acyclic (no cycles).", "tasks");

        foreach (var task in model.Tasks)
        {
            if (!reachable.Contains(task.InstanceId))
                result.Add("graph.unreachable", "All tasks must be reachable from startingTaskInstanceId.", $"tasks[{nodeIndex[task.InstanceId]}]");
        }

        // Enforce: from-task output channel can connect to many nodes, but each destination task must have <= 1 incoming edge.
        // NOTE: "destination input channel" is not modeled yet; this is the strictest safe check we can do with current DTOs.
        var incomingCounts = new Dictionary<Guid, int>();
        foreach (var task in model.Tasks)
        {
            foreach (var conn in task.OutgoingConnections)
            {
                if (!tasksByInstanceId.ContainsKey(conn.ConnectedTaskInstanceId))
                    continue;

                incomingCounts.TryGetValue(conn.ConnectedTaskInstanceId, out var count);
                incomingCounts[conn.ConnectedTaskInstanceId] = count + 1;
            }
        }

        foreach (var (destId, count) in incomingCounts)
        {
            if (destId == model.StartingTaskInstanceId)
            {
                if (count > 0)
                    result.Add("graph.start.incoming", "Starting task must not have any incoming connections.", "startingTaskInstanceId");
                continue;
            }

            if (count > 1)
                result.Add("graph.node.multipleIncoming", "Task must not have multiple incoming connections.", $"tasks[{nodeIndex[destId]}]");
        }

        return Task.FromResult(result);
    }
}
