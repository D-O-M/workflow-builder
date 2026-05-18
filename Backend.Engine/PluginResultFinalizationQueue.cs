using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Backend.Plugins.Communication;

namespace Backend.Engine;

public sealed record PluginResultFinalizationWorkItem(
    PluginExecutionResult Result,
    DateTimeOffset EnqueuedAt);

public sealed class PluginResultFinalizationQueue
{
    private readonly Channel<QueuedPluginResult> _channel;
    private readonly PluginResultFinalizationTelemetry _telemetry;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _depthGate = new();
    private readonly LinkedList<DateTimeOffset> _queuedResultTimes = new();

    public PluginResultFinalizationQueue(
        PluginRuntimeOptions options,
        PluginResultFinalizationTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(telemetry);
        options.Validate();

        _telemetry = telemetry;
        _channel = Channel.CreateBounded<QueuedPluginResult>(
            new BoundedChannelOptions(options.EffectiveResultFinalizationQueueCapacity)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = options.ResultFinalizationQueueFullMode,
                AllowSynchronousContinuations = false
            });

        PublishQueueSnapshot(DateTimeOffset.UtcNow);
    }

    public int Depth
    {
        get
        {
            lock (_depthGate)
            {
                return _queuedResultTimes.Count;
            }
        }
    }

    public ValueTask EnqueueAsync(
        PluginExecutionResult result,
        CancellationToken cancellationToken) =>
        new(EnqueueCoreAsync(result, cancellationToken));

    public void Complete(Exception? error = null) =>
        _channel.Writer.TryComplete(error);

    public async IAsyncEnumerable<PluginResultFinalizationWorkItem> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            MarkDequeued(item.QueueNode);
            yield return new PluginResultFinalizationWorkItem(item.Result, item.EnqueuedAt);
        }
    }

    private async Task EnqueueCoreAsync(
        PluginExecutionResult result,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            while (await _channel.Writer.WaitToWriteAsync(cancellationToken))
            {
                var enqueuedAt = DateTimeOffset.UtcNow;
                var queueNode = MarkEnqueued(enqueuedAt);
                if (_channel.Writer.TryWrite(new QueuedPluginResult(result, enqueuedAt, queueNode)))
                {
                    stopwatch.Stop();
                    _telemetry.RecordEnqueueWait(stopwatch.Elapsed);
                    return;
                }

                MarkDequeued(queueNode);
            }
        }
        finally
        {
            _writeGate.Release();
        }

        throw new ChannelClosedException("The plugin result finalization queue is closed.");
    }

    private LinkedListNode<DateTimeOffset> MarkEnqueued(DateTimeOffset enqueuedAt)
    {
        lock (_depthGate)
        {
            var node = _queuedResultTimes.AddLast(enqueuedAt);
            PublishQueueSnapshotCore(enqueuedAt);
            return node;
        }
    }

    private void MarkDequeued(LinkedListNode<DateTimeOffset> queueNode)
    {
        lock (_depthGate)
        {
            if (queueNode.List is not null)
            {
                _queuedResultTimes.Remove(queueNode);
            }

            PublishQueueSnapshotCore(DateTimeOffset.UtcNow);
        }
    }

    private void PublishQueueSnapshot(DateTimeOffset observedAt)
    {
        lock (_depthGate)
        {
            PublishQueueSnapshotCore(observedAt);
        }
    }

    private void PublishQueueSnapshotCore(DateTimeOffset observedAt)
    {
        var oldest = _queuedResultTimes.First?.Value;
        _telemetry.RecordQueueSnapshot(_queuedResultTimes.Count, oldest, observedAt);
    }

    private sealed record QueuedPluginResult(
        PluginExecutionResult Result,
        DateTimeOffset EnqueuedAt,
        LinkedListNode<DateTimeOffset> QueueNode);
}
