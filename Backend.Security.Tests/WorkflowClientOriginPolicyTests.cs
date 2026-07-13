using Backend.Security.Cors;
using Shouldly;

namespace Backend.Security.Tests;

public sealed class WorkflowClientOriginPolicyTests
{
    [Test]
    public void Configured_cloud_origin_is_allowed()
    {
        var allowed = WorkflowClientOriginPolicy.IsAllowed(
            "https://workflow.example.test",
            ["https://workflow.example.test/"]);

        allowed.ShouldBeTrue();
    }

    [Test]
    public void Unconfigured_cloud_origin_is_rejected()
    {
        var allowed = WorkflowClientOriginPolicy.IsAllowed(
            "https://unrelated.example.test",
            ["https://workflow.example.test"]);

        allowed.ShouldBeFalse();
    }

    [Test]
    public void Loopback_origin_remains_available_for_local_development()
    {
        WorkflowClientOriginPolicy.IsAllowed("http://127.0.0.1:4200", []).ShouldBeTrue();
    }
}
