using Backend.Plugins.Communication;
using Shouldly;

namespace WorkflowBuilder.IntegrationTests.Infrastructure;

public sealed class InMemoryWorkflowTaskQueueTests
{
    [Test]
    public async Task Abandon_requeues_the_same_payload(
        CancellationToken cancellationToken)
    {
        var queue = new InMemoryWorkflowTaskQueue();
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
        var queue = new InMemoryWorkflowTaskQueue();
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

    private static WorkflowTaskInitialisationMessage CreateMessage() =>
        new()
        {
            WorkflowInstanceId = Guid.NewGuid(),
            WorkflowBlueprintId = Random.Shared.Next(1, int.MaxValue),
            WorkflowTaskInstanceId = Guid.NewGuid(),
            BlueprintTaskId = Guid.NewGuid(),
            PreviousWorkflowTaskInstanceId = Guid.NewGuid(),
            LinkedOutputChannelName = "Success"
        };
}
