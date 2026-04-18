namespace Backend.Engine;

public interface IAgentManager
{
    bool AgentExists(int agentId);
    void RegisterAgent(int agentId, NanoServiceAgent agent);
    void UnregisterAgent(int agentId);
    NanoServiceAgent GetAgent(int agentId);
    bool TryAcquireJobSlot(int agentId);
    void ReleaseJobSlot(int agentId);
    int GetActiveJobCount(int agentId);
}

public class AgentManager : IAgentManager
{
    private readonly Dictionary<int, NanoServiceAgent> _agents = new();
    private readonly Dictionary<int, int> _activeJobCounts = new();
    private readonly Lock _sync = new();

    public void RegisterAgent(int agentId, NanoServiceAgent agent)
    {
        lock (_sync)
        {
            if (_agents.ContainsKey(agentId))
            {
                throw new InvalidOperationException($"Agent with ID {agentId} is already registered.");
            }

            _agents[agentId] = agent;
            _activeJobCounts.TryAdd(agentId, 0);
        }
    }

    public bool AgentExists(int agentId)
    {
        lock (_sync)
        {
            return _agents.ContainsKey(agentId);
        }
    }

    public NanoServiceAgent GetAgent(int agentId)
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
            _activeJobCounts.Remove(agentId);
        }
    }

    public bool TryAcquireJobSlot(int agentId)
    {
        lock (_sync)
        {
            if (!_agents.ContainsKey(agentId))
            {
                return false;
            }

            _activeJobCounts.TryGetValue(agentId, out var activeJobs);
            _activeJobCounts[agentId] = activeJobs + 1;
            return true;
        }
    }

    public void ReleaseJobSlot(int agentId)
    {
        lock (_sync)
        {
            if (!_activeJobCounts.TryGetValue(agentId, out var activeJobs))
            {
                return;
            }

            _activeJobCounts[agentId] = Math.Max(activeJobs - 1, 0);
        }
    }

    public int GetActiveJobCount(int agentId)
    {
        lock (_sync)
        {
            return _activeJobCounts.TryGetValue(agentId, out var activeJobs) ? activeJobs : 0;
        }
    }

    /*public bool TryQueueTask(TaskContext taskContext)
    {
        if (!AgentExists(taskContext.SchemaId))
            throw new Exception();

        var agent = _agents[taskContext.SchemaId];

    }*/
}
