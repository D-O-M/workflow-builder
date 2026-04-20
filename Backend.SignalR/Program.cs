using Backend.Models.Realtime;
using Backend.SignalR.Hubs;
using Backend.SignalR.Services;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Services.AddSignalR();
builder.Services.AddSingleton<ExecutionSubscriptionRegistry>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngularDevClient", policy =>
    {
        policy.SetIsOriginAllowed(IsAllowedDevelopmentOrigin)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();

app.MapDefaultEndpoints();
app.UseCors("AllowAngularDevClient");

app.MapHub<ExecutionProgressHub>("/hubs/executions");
app.MapGet("/internal/subscriptions/snapshot", (ExecutionSubscriptionRegistry registry) =>
    Results.Ok(registry.GetSnapshot()));
app.MapPost(
    "/internal/execution-events",
    async Task<IResult> (
        ExecutionRealtimeBatch batch,
        ExecutionSubscriptionRegistry registry,
        IHubContext<ExecutionProgressHub> hubContext,
        CancellationToken cancellationToken) =>
    {
        if (batch.Summaries.Count == 0 && batch.NodeUpdates.Count == 0)
        {
            return Results.Accepted();
        }

        foreach (var summary in batch.Summaries)
        {
            if (!registry.HasBlueprintSubscribers(summary.BlueprintDefinitionId))
            {
                continue;
            }

            await hubContext.Clients
                .Group(ExecutionHubGroups.Blueprint(summary.BlueprintDefinitionId))
                .SendAsync("executionSummaryUpdated", summary, cancellationToken);
        }

        foreach (var nodeUpdate in batch.NodeUpdates.Where(update => update.Nodes.Count > 0))
        {
            if (!registry.HasWorkflowSubscribers(nodeUpdate.WorkflowInstanceId))
            {
                continue;
            }

            await hubContext.Clients
                .Group(ExecutionHubGroups.Workflow(nodeUpdate.WorkflowInstanceId))
                .SendAsync("executionNodesUpdated", nodeUpdate, cancellationToken);
        }

        return Results.Accepted();
    });

app.Run();

static bool IsAllowedDevelopmentOrigin(string origin)
{
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
    {
        return false;
    }

    return uri.IsLoopback
        || string.Equals(uri.Host, "host.docker.internal", StringComparison.OrdinalIgnoreCase);
}
