using System.Threading.Channels;
using Backend.Interfaces.Queues;
using Backend.Plugins.Communication;

namespace WorkflowBuilder.IntegrationTests.Infrastructure;

internal sealed class InMemoryWorkflowTaskQueue
    : IWorkflowTaskQueueReader,
      IWorkflowTaskQueueWriter
{
    private readonly Channel<QueueEnvelope> _channel = Channel.CreateUnbounded<QueueEnvelope>();

    public Task EnqueueAsync(
        WorkflowTaskInitialisationMessage message,
        CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(new QueueEnvelope(message), cancellationToken).AsTask();

    public async Task<IWorkflowTaskQueueMessage?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var envelope = await _channel.Reader.ReadAsync(cancellationToken);
        return new InMemoryWorkflowTaskQueueMessage(this, envelope);
    }

    private Task RequeueAsync(QueueEnvelope envelope, CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(new QueueEnvelope(envelope.Payload), cancellationToken).AsTask();

    private sealed record QueueEnvelope(
        WorkflowTaskInitialisationMessage Payload,
        string MessageId,
        DateTimeOffset LockedUntil)
    {
        public QueueEnvelope(WorkflowTaskInitialisationMessage payload)
            : this(payload, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddMinutes(1))
        {
        }
    }

    private sealed class InMemoryWorkflowTaskQueueMessage : IWorkflowTaskQueueMessage
    {
        private readonly InMemoryWorkflowTaskQueue _queue;
        private readonly QueueEnvelope _envelope;
        private int _settled;

        public InMemoryWorkflowTaskQueueMessage(
            InMemoryWorkflowTaskQueue queue,
            QueueEnvelope envelope)
        {
            _queue = queue;
            _envelope = envelope;
            LockedUntil = envelope.LockedUntil;
        }

        public string MessageId => _envelope.MessageId;

        public DateTimeOffset LockedUntil { get; private set; }

        public WorkflowTaskInitialisationMessage Payload => _envelope.Payload;

        public Task CompleteAsync(CancellationToken cancellationToken)
        {
            Interlocked.Exchange(ref _settled, 1);
            return Task.CompletedTask;
        }

        public async Task AbandonAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _settled, 1) != 0)
            {
                return;
            }

            await _queue.RequeueAsync(_envelope, cancellationToken);
        }

        public Task RenewLockAsync(CancellationToken cancellationToken)
        {
            LockedUntil = DateTimeOffset.UtcNow.AddMinutes(1);
            return Task.CompletedTask;
        }
    }
}
