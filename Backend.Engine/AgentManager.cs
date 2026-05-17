namespace Backend.Engine;

public interface IAgentManager
{
    bool AgentExists(int agentId);
    void RegisterAgent(int agentId, PluginRuntimeAgent agent);
    void UnregisterAgent(int agentId);
    PluginRuntimeAgent GetAgent(int agentId);
    bool TryAcquireStepSlot(int agentId);
    void ReleaseStepSlot(int agentId);
    int GetActiveStepCount(int agentId);
}

public class AgentManager : IAgentManager
{
    private readonly Dictionary<int, PluginRuntimeAgent> _agents = new();
    private readonly Dictionary<int, int> _activeStepCounts = new();
    private readonly Lock _sync = new();

    public void RegisterAgent(int agentId, PluginRuntimeAgent agent)
    {
        lock (_sync)
        {
            if (_agents.ContainsKey(agentId))
            {
                throw new InvalidOperationException($"Agent with ID {agentId} is already registered.");
            }

            _agents[agentId] = agent;
            _activeStepCounts.TryAdd(agentId, 0);
        }
    }

    public bool AgentExists(int agentId)
    {
        lock (_sync)
        {
            return _agents.ContainsKey(agentId);
        }
    }

    public PluginRuntimeAgent GetAgent(int agentId)
    {
        lock (_sync)
        {
            if (!_agents.TryGetValue(agentId, out var agent))
            {
                throw new InvalidOperationException($"Agent with ID {agentId} is not registered.");
            }

            return agent;
        }
    }

    public void UnregisterAgent(int agentId)
    {
        lock (_sync)
        {
            _agents.Remove(agentId);
            _activeStepCounts.Remove(agentId);
        }
    }

    public bool TryAcquireStepSlot(int agentId)
    {
        lock (_sync)
        {
            if (!_agents.ContainsKey(agentId))
            {
                return false;
            }

            _activeStepCounts.TryGetValue(agentId, out var activeSteps);
            _activeStepCounts[agentId] = activeSteps + 1;
            return true;
        }
    }

    public void ReleaseStepSlot(int agentId)
    {
        lock (_sync)
        {
            if (!_activeStepCounts.TryGetValue(agentId, out var activeSteps))
            {
                return;
            }

            _activeStepCounts[agentId] = Math.Max(activeSteps - 1, 0);
        }
    }

    public int GetActiveStepCount(int agentId)
    {
        lock (_sync)
        {
            return _activeStepCounts.TryGetValue(agentId, out var activeSteps) ? activeSteps : 0;
        }
    }

    /*public bool TryQueueTask(TaskContext taskContext)
    {
        if (!AgentExists(taskContext.SchemaId))
            throw new Exception();

        var agent = _agents[taskContext.SchemaId];

    }*/
}
