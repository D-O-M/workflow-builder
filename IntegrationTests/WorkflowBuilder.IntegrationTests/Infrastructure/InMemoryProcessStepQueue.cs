using System.Threading.Channels;
using Backend.Interfaces.Queues;
using Backend.Plugins.Communication;

namespace WorkflowBuilder.IntegrationTests.Infrastructure;

internal sealed class InMemoryProcessStepQueue
    : IProcessStepQueueProcessor,
      IProcessStepQueueWriter
{
    private readonly Channel<QueueEnvelope> _channel = Channel.CreateUnbounded<QueueEnvelope>();
    private CancellationTokenSource? _processingCts;
    private Task? _processingTask;

    public Task EnqueueAsync(
        ProcessStepInitialisationMessage message,
        CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(new QueueEnvelope(message), cancellationToken).AsTask();

    public async Task<IProcessStepQueueMessage?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var envelope = await _channel.Reader.ReadAsync(cancellationToken);
        return new InMemoryProcessStepQueueMessage(this, envelope);
    }

    public Task StartProcessingAsync(
        Func<IProcessStepQueueMessage, CancellationToken, Task> processMessageAsync,
        CancellationToken cancellationToken)
    {
        if (_processingTask is not null)
        {
            throw new InvalidOperationException("In-memory process-step queue is already processing.");
        }

        _processingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _processingTask = Task.Run(
            () => ProcessAsync(processMessageAsync, _processingCts.Token),
            CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopProcessingAsync(CancellationToken cancellationToken)
    {
        if (_processingCts is null || _processingTask is null)
        {
            return;
        }

        await _processingCts.CancelAsync();
        try
        {
            await _processingTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        finally
        {
            _processingCts.Dispose();
            _processingCts = null;
            _processingTask = null;
        }
    }

    private async Task ProcessAsync(
        Func<IProcessStepQueueMessage, CancellationToken, Task> processMessageAsync,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await ReceiveAsync(cancellationToken);
            if (message is not null)
            {
                await processMessageAsync(message, cancellationToken);
            }
        }
    }

    private Task RequeueAsync(QueueEnvelope envelope, CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(new QueueEnvelope(envelope.Payload), cancellationToken).AsTask();

    private sealed record QueueEnvelope(
        ProcessStepInitialisationMessage Payload,
        string MessageId,
        DateTimeOffset EnqueuedAt,
        DateTimeOffset LockedUntil)
    {
        public QueueEnvelope(ProcessStepInitialisationMessage payload)
            : this(payload, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1))
        {
        }
    }

    private sealed class InMemoryProcessStepQueueMessage : IProcessStepQueueMessage
    {
        private readonly InMemoryProcessStepQueue _queue;
        private readonly QueueEnvelope _envelope;
        private int _settled;

        public InMemoryProcessStepQueueMessage(
            InMemoryProcessStepQueue queue,
            QueueEnvelope envelope)
        {
            _queue = queue;
            _envelope = envelope;
            LockedUntil = envelope.LockedUntil;
        }

        public string MessageId => _envelope.MessageId;

        public DateTimeOffset EnqueuedAt => _envelope.EnqueuedAt;

        public DateTimeOffset LockedUntil { get; private set; }

        public ProcessStepInitialisationMessage Payload => _envelope.Payload;

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
