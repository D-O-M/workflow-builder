using Backend.Models.Realtime;
using Backend.SignalR.Hubs;
using Backend.SignalR.Services;
using Microsoft.AspNetCore.SignalR;

AspireEndpointEnvironment.PreferAspireHttpPortsOverInheritedUrls();
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Services.AddSignalR();
builder.Services.AddSingleton<ProcessSubscriptionRegistry>();
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

app.MapHub<ProcessProgressHub>("/hubs/processes");
app.MapGet("/internal/subscriptions/snapshot", (ProcessSubscriptionRegistry registry) =>
    Results.Ok(registry.GetSnapshot()));
app.MapPost(
    "/internal/process-events",
    async Task<IResult> (
        ProcessRealtimeBatch batch,
        ProcessSubscriptionRegistry registry,
        IHubContext<ProcessProgressHub> hubContext,
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
                .Group(ProcessHubGroups.Blueprint(summary.BlueprintDefinitionId))
                .SendAsync("processSummaryUpdated", summary, cancellationToken);
        }

        foreach (var nodeUpdate in batch.NodeUpdates.Where(update => update.Nodes.Count > 0))
        {
            if (!registry.HasProcessSubscribers(nodeUpdate.ProcessId))
            {
                continue;
            }

            await hubContext.Clients
                .Group(ProcessHubGroups.Process(nodeUpdate.ProcessId))
                .SendAsync("processStepsUpdated", nodeUpdate, cancellationToken);
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
