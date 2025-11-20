namespace Backend.Engine;

public interface IAgentManager
{
    bool AgentExists(int agentId);
    void RegisterAgent(int agentId, NanoServiceAgent agent);
    NanoServiceAgent GetAgent(int agentId);
}

public class AgentManager : IAgentManager
{
    private readonly Dictionary<int, NanoServiceAgent> _agents = new();

    public void RegisterAgent(int agentId, NanoServiceAgent agent)
    {
        if (AgentExists(agentId))
        {
            throw new InvalidOperationException($"Agent with ID {agentId} is already registered.");
        }

        _agents[agentId] = agent;
    }

    public bool AgentExists(int agentId)
    {
        return _agents.ContainsKey(agentId);
    }

    public NanoServiceAgent GetAgent(int agentId)
    {
        if (!_agents.TryGetValue(agentId, out var agent))
        {
            throw new InvalidOperationException($"Agent with ID {agentId} is not registered.");
        }

        return agent;
    }

    /*public bool TryQueueTask(TaskContext taskContext)
    {
        if (!AgentExists(taskContext.SchemaId))
            throw new Exception();

        var agent = _agents[taskContext.SchemaId];

    }*/
}
