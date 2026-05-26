using System.Threading.Channels;

namespace Backend.Engine;

public sealed class PluginAgentHealthEventBus
{
    private readonly Channel<PluginAgentHealthEvent> _channel =
        Channel.CreateUnbounded<PluginAgentHealthEvent>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

    public ValueTask PublishAsync(
        PluginAgentHealthEvent healthEvent,
        CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(healthEvent, cancellationToken);

    public IAsyncEnumerable<PluginAgentHealthEvent> ReadAllAsync(
        CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
