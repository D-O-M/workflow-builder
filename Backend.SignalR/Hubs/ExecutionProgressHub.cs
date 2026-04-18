using Backend.SignalR.Services;
using Microsoft.AspNetCore.SignalR;

namespace Backend.SignalR.Hubs;

public sealed class ExecutionProgressHub : Hub
{
    private readonly ExecutionSubscriptionRegistry _registry;

    public ExecutionProgressHub(ExecutionSubscriptionRegistry registry)
    {
        _registry = registry;
    }

    public async Task SubscribeBlueprint(int blueprintDefinitionId)
    {
        var group = ExecutionHubGroups.Blueprint(blueprintDefinitionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
        _registry.Subscribe(Context.ConnectionId, group);
    }

    public async Task UnsubscribeBlueprint(int blueprintDefinitionId)
    {
        var group = ExecutionHubGroups.Blueprint(blueprintDefinitionId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
        _registry.Unsubscribe(Context.ConnectionId, group);
    }

    public async Task SubscribeWorkflow(Guid workflowInstanceId)
    {
        var group = ExecutionHubGroups.Workflow(workflowInstanceId);
        await Groups.AddToGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
        _registry.Subscribe(Context.ConnectionId, group);
    }

    public async Task UnsubscribeWorkflow(Guid workflowInstanceId)
    {
        var group = ExecutionHubGroups.Workflow(workflowInstanceId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
        _registry.Unsubscribe(Context.ConnectionId, group);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _registry.RemoveConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
