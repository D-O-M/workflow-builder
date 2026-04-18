using System.Threading.Channels;
using Backend.Interfaces.Queues;
using Backend.Plugins.Communication;

namespace WorkflowBuilder.IntegrationTests.Infrastructure;

internal sealed class InMemoryPluginReloadQueue
    : IPluginReloadQueueReader,
      IPluginReloadQueueWriter
{
    private readonly Channel<QueueEnvelope> _channel = Channel.CreateUnbounded<QueueEnvelope>();

    public Task EnqueueAsync(
        PluginReloadRequestMessage message,
        CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(new QueueEnvelope(message), cancellationToken).AsTask();

    public async Task<IPluginReloadQueueMessage?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var envelope = await _channel.Reader.ReadAsync(cancellationToken);
        return new InMemoryPluginReloadQueueMessage(this, envelope);
    }

    private Task RequeueAsync(QueueEnvelope envelope, CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(new QueueEnvelope(envelope.Payload), cancellationToken).AsTask();

    private sealed record QueueEnvelope(
        PluginReloadRequestMessage Payload,
        string MessageId,
        DateTimeOffset LockedUntil)
    {
        public QueueEnvelope(PluginReloadRequestMessage payload)
            : this(payload, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddMinutes(1))
        {
        }
    }

    private sealed class InMemoryPluginReloadQueueMessage : IPluginReloadQueueMessage
    {
        private readonly InMemoryPluginReloadQueue _queue;
        private readonly QueueEnvelope _envelope;
        private int _settled;

        public InMemoryPluginReloadQueueMessage(
            InMemoryPluginReloadQueue queue,
            QueueEnvelope envelope)
        {
            _queue = queue;
            _envelope = envelope;
            LockedUntil = envelope.LockedUntil;
        }

        public string MessageId => _envelope.MessageId;

        public DateTimeOffset LockedUntil { get; private set; }

        public PluginReloadRequestMessage Payload => _envelope.Payload;

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
