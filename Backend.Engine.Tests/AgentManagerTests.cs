using Shouldly;

namespace Backend.Engine.Tests;

public sealed class AgentManagerTests
{
    [Test]
    public void TryAcquireStepSlot_does_not_enforce_per_agent_limit()
    {
        var manager = CreateManager();
        manager.RegisterAgent(101, null!);

        manager.TryAcquireStepSlot(101).ShouldBeTrue();
        manager.TryAcquireStepSlot(101).ShouldBeTrue();
        manager.TryAcquireStepSlot(101).ShouldBeTrue();

        manager.ReleaseStepSlot(101);

        manager.TryAcquireStepSlot(101).ShouldBeTrue();
        manager.GetActiveStepCount(101).ShouldBe(3);
    }

    [Test]
    public void TryAcquireStepSlot_does_not_enforce_total_limit_across_agents()
    {
        var manager = CreateManager();
        manager.RegisterAgent(101, null!);
        manager.RegisterAgent(202, null!);

        manager.TryAcquireStepSlot(101).ShouldBeTrue();
        manager.TryAcquireStepSlot(202).ShouldBeTrue();
        manager.TryAcquireStepSlot(101).ShouldBeTrue();

        manager.ReleaseStepSlot(202);

        manager.TryAcquireStepSlot(101).ShouldBeTrue();
        manager.GetActiveStepCount(101).ShouldBe(3);
    }

    [Test]
    public void TryAcquireStepSlot_rejects_unknown_agent()
    {
        var manager = CreateManager();

        manager.TryAcquireStepSlot(404).ShouldBeFalse();
    }

    [Test]
    public void UnregisterAgent_removes_active_job_tracking()
    {
        var manager = CreateManager();
        manager.RegisterAgent(101, null!);
        manager.RegisterAgent(202, null!);

        manager.TryAcquireStepSlot(101).ShouldBeTrue();
        manager.TryAcquireStepSlot(101).ShouldBeTrue();

        manager.UnregisterAgent(101);

        manager.GetActiveStepCount(101).ShouldBe(0);
        manager.TryAcquireStepSlot(202).ShouldBeTrue();
    }

    private static AgentManager CreateManager() => new();
}
