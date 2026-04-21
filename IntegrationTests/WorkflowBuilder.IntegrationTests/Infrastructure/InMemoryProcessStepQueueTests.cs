using Backend.Plugins.Communication;
using Shouldly;

namespace WorkflowBuilder.IntegrationTests.Infrastructure;

public sealed class InMemoryProcessStepQueueTests
{
    [Test]
    public async Task Abandon_requeues_the_same_payload(
        CancellationToken cancellationToken)
    {
        var queue = new InMemoryProcessStepQueue();
        var message = CreateMessage();

        await queue.EnqueueAsync(message, cancellationToken);

        var leasedMessage = await queue.ReceiveAsync(cancellationToken);
        leasedMessage.ShouldNotBeNull();
        leasedMessage.Payload.ShouldBe(message);

        await leasedMessage.AbandonAsync(cancellationToken);

        var requeuedMessage = await queue.ReceiveAsync(cancellationToken);
        requeuedMessage.ShouldNotBeNull();
        requeuedMessage.Payload.ShouldBe(message);
        requeuedMessage.MessageId.ShouldNotBe(leasedMessage.MessageId);
    }

    [Test]
    public async Task Renew_lock_extends_visibility_and_complete_does_not_requeue(
        CancellationToken cancellationToken)
    {
        var queue = new InMemoryProcessStepQueue();
        var firstMessage = CreateMessage();
        var secondMessage = CreateMessage();

        await queue.EnqueueAsync(firstMessage, cancellationToken);

        var leasedMessage = await queue.ReceiveAsync(cancellationToken);
        leasedMessage.ShouldNotBeNull();

        var originalLock = leasedMessage.LockedUntil;
        await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        await leasedMessage.RenewLockAsync(cancellationToken);
        leasedMessage.LockedUntil.ShouldBeGreaterThan(originalLock);

        await leasedMessage.CompleteAsync(cancellationToken);
        await leasedMessage.AbandonAsync(cancellationToken);

        await queue.EnqueueAsync(secondMessage, cancellationToken);

        var nextMessage = await queue.ReceiveAsync(cancellationToken);
        nextMessage.ShouldNotBeNull();
        nextMessage.Payload.ShouldBe(secondMessage);
    }

    private static ProcessStepInitialisationMessage CreateMessage() =>
        new()
        {
            ProcessId = Guid.NewGuid(),
            ExecutableBlueprintId = Random.Shared.Next(1, int.MaxValue),
            ProcessStepId = Guid.NewGuid(),
            BlueprintNodeId = Guid.NewGuid()
        };
}
