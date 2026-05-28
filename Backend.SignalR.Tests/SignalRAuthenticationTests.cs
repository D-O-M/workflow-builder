using System.Reflection;
using Backend.Security.Authorization;
using Backend.SignalR.Hubs;
using Microsoft.AspNetCore.Authorization;
using Shouldly;

namespace Backend.SignalR.Tests;

public sealed class SignalRAuthenticationTests
{
    [Test]
    public void Process_progress_hub_requires_workflow_authentication()
    {
        var authorize = typeof(ProcessProgressHub).GetCustomAttribute<AuthorizeAttribute>();

        authorize.ShouldNotBeNull();
        authorize.Policy.ShouldBe(WorkflowAuthorizationPolicies.Authenticated);
    }

    [Test]
    public void Signalr_access_token_query_binding_is_scoped_to_process_hub()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "Backend.SignalR",
            "Program.cs"));
        var source = File.ReadAllText(sourcePath);

        source.ShouldContain("Query[\"access_token\"]");
        source.ShouldContain("WorkflowBuilderTopology.Realtime.HubRoute");
        source.ShouldContain("InternalProcessEventsRoute");
    }
}
