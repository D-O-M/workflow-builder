using Backend.Models.Realtime;
using Backend.Security.Authentication;
using Backend.Security.Authorization;
using Backend.SignalR.Hubs;
using Backend.SignalR.Services;
using Backend.Topology;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Security.Cryptography;
using System.Text;

AspireEndpointEnvironment.PreferAspireHttpPortsOverInheritedUrls();
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
WorkflowBuilderTopologyValidator.ValidateSignalR();

builder.Services.AddWorkflowSecurity(builder.Configuration, builder.Environment, options =>
{
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            if (!string.IsNullOrWhiteSpace(accessToken) &&
                context.HttpContext.Request.Path.Equals(
                    WorkflowBuilderTopology.Realtime.HubRoute,
                    StringComparison.OrdinalIgnoreCase))
            {
                context.Token = accessToken;
            }

            return Task.CompletedTask;
        }
    };
});
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
app.UseAuthentication();
app.UseAuthorization();

app.MapHub<ProcessProgressHub>(WorkflowBuilderTopology.Realtime.HubRoute);
app.MapGet(
    WorkflowBuilderTopology.Realtime.InternalSubscriptionSnapshotRoute,
    async Task<IResult> (
        ProcessSubscriptionRegistry registry,
        HttpContext httpContext,
        IConfiguration configuration,
        IHostEnvironment environment,
        IAuthorizationService authorizationService) =>
    {
        var authorizationFailure = await AuthorizeInternalPublisherAsync(
            httpContext,
            configuration,
            environment,
            authorizationService);
        return authorizationFailure ?? Results.Ok(registry.GetSnapshot());
    });
app.MapPost(
    WorkflowBuilderTopology.Realtime.InternalProcessEventsRoute,
    async Task<IResult> (
        ProcessRealtimeBatch batch,
        ProcessSubscriptionRegistry registry,
        IHubContext<ProcessProgressHub> hubContext,
        HttpContext httpContext,
        IConfiguration configuration,
        IHostEnvironment environment,
        IAuthorizationService authorizationService,
        CancellationToken cancellationToken) =>
    {
        var authorizationFailure = await AuthorizeInternalPublisherAsync(
            httpContext,
            configuration,
            environment,
            authorizationService);
        if (authorizationFailure is not null)
        {
            return authorizationFailure;
        }

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
                .SendAsync(WorkflowBuilderTopology.Realtime.ProcessSummaryUpdatedEvent, summary, cancellationToken);
        }

        foreach (var nodeUpdate in batch.NodeUpdates.Where(update => update.Nodes.Count > 0))
        {
            if (!registry.HasProcessSubscribers(nodeUpdate.ProcessId))
            {
                continue;
            }

            await hubContext.Clients
                .Group(ProcessHubGroups.Process(nodeUpdate.ProcessId))
                .SendAsync(WorkflowBuilderTopology.Realtime.ProcessStepsUpdatedEvent, nodeUpdate, cancellationToken);
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

static async Task<IResult?> AuthorizeInternalPublisherAsync(
    HttpContext httpContext,
    IConfiguration configuration,
    IHostEnvironment environment,
    IAuthorizationService authorizationService)
{
    if (configuration.GetValue("Authentication:Enabled", false))
    {
        var result = await authorizationService.AuthorizeAsync(
            httpContext.User,
            WorkflowAuthorizationPolicies.InternalPublisher);
        if (result.Succeeded)
        {
            return null;
        }
    }

    var configuredSecret = configuration[WorkflowBuilderTopology.Realtime.InternalPublisherSharedSecretKey];
    if (string.IsNullOrWhiteSpace(configuredSecret))
    {
        return environment.IsDevelopment()
            ? null
            : Results.Unauthorized();
    }

    if (!httpContext.Request.Headers.TryGetValue(
            WorkflowBuilderTopology.Realtime.InternalPublisherHeaderName,
            out var providedSecret))
    {
        return Results.Unauthorized();
    }

    return FixedTimeEquals(providedSecret.ToString(), configuredSecret)
        ? null
        : Results.Unauthorized();
}

static bool FixedTimeEquals(string provided, string expected)
{
    var providedBytes = Encoding.UTF8.GetBytes(provided);
    var expectedBytes = Encoding.UTF8.GetBytes(expected);
    return providedBytes.Length == expectedBytes.Length &&
        CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
}
