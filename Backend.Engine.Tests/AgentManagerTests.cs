using Shouldly;

namespace Backend.Engine.Tests;

public sealed class AgentManagerTests
{
    [Test]
    public void TryAcquireJobSlot_does_not_enforce_per_agent_limit()
    {
        var manager = CreateManager();
        manager.RegisterAgent(101, null!);

        manager.TryAcquireJobSlot(101).ShouldBeTrue();
        manager.TryAcquireJobSlot(101).ShouldBeTrue();
        manager.TryAcquireJobSlot(101).ShouldBeTrue();

        manager.ReleaseJobSlot(101);

        manager.TryAcquireJobSlot(101).ShouldBeTrue();
        manager.GetActiveJobCount(101).ShouldBe(3);
    }

    [Test]
    public void TryAcquireJobSlot_does_not_enforce_total_limit_across_agents()
    {
        var manager = CreateManager();
        manager.RegisterAgent(101, null!);
        manager.RegisterAgent(202, null!);

        manager.TryAcquireJobSlot(101).ShouldBeTrue();
        manager.TryAcquireJobSlot(202).ShouldBeTrue();
        manager.TryAcquireJobSlot(101).ShouldBeTrue();

        manager.ReleaseJobSlot(202);

        manager.TryAcquireJobSlot(101).ShouldBeTrue();
        manager.GetActiveJobCount(101).ShouldBe(3);
    }

    [Test]
    public void TryAcquireJobSlot_rejects_unknown_agent()
    {
        var manager = CreateManager();

        manager.TryAcquireJobSlot(404).ShouldBeFalse();
    }

    [Test]
    public void UnregisterAgent_removes_active_job_tracking()
    {
        var manager = CreateManager();
        manager.RegisterAgent(101, null!);
        manager.RegisterAgent(202, null!);

        manager.TryAcquireJobSlot(101).ShouldBeTrue();
        manager.TryAcquireJobSlot(101).ShouldBeTrue();

        manager.UnregisterAgent(101);

        manager.GetActiveJobCount(101).ShouldBe(0);
        manager.TryAcquireJobSlot(202).ShouldBeTrue();
    }

    private static AgentManager CreateManager() => new();
}
