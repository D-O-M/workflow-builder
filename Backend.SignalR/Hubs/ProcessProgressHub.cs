using Backend.SignalR.Services;
using Backend.Security.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Backend.SignalR.Hubs;

[Authorize(Policy = WorkflowAuthorizationPolicies.Authenticated)]
public sealed class ProcessProgressHub : Hub
{
    private readonly ProcessSubscriptionRegistry _registry;

    public ProcessProgressHub(ProcessSubscriptionRegistry registry)
    {
        _registry = registry;
    }

    public async Task SubscribeBlueprint(int blueprintDefinitionId)
    {
        // TODO: call IWorkflowAuthorizationService for resource-level blueprint subscriptions.
        var group = ProcessHubGroups.Blueprint(blueprintDefinitionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
        _registry.Subscribe(Context.ConnectionId, group);
    }

    public async Task UnsubscribeBlueprint(int blueprintDefinitionId)
    {
        var group = ProcessHubGroups.Blueprint(blueprintDefinitionId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
        _registry.Unsubscribe(Context.ConnectionId, group);
    }

    public async Task SubscribeProcess(Guid processId)
    {
        // TODO: call IWorkflowAuthorizationService for resource-level process subscriptions.
        var group = ProcessHubGroups.Process(processId);
        await Groups.AddToGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
        _registry.Subscribe(Context.ConnectionId, group);
    }

    public async Task UnsubscribeProcess(Guid processId)
    {
        var group = ProcessHubGroups.Process(processId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
        _registry.Unsubscribe(Context.ConnectionId, group);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _registry.RemoveConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
