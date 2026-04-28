using System.Threading.Channels;
using Backend.Plugins.Communication;

namespace Backend.Engine;

public sealed class PluginResultFinalizationQueue
{
    private readonly Channel<PluginExecutionResult> _channel;

    public PluginResultFinalizationQueue()
    {
        _channel = Channel.CreateUnbounded<PluginExecutionResult>(
            new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
    }

    public ValueTask EnqueueAsync(
        PluginExecutionResult result,
        CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(result, cancellationToken);

    public IAsyncEnumerable<PluginExecutionResult> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
