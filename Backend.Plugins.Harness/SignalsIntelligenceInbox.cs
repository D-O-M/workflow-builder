using System.Threading.Channels;

namespace Backend.Plugins.Harness;

internal enum SignalsIntelligenceMessageKind
{
    InputChannelModel,
    GlobalState
}

internal sealed record SignalsIntelligenceMessage(
    SignalsIntelligenceMessageKind Kind,
    string Identifier,
    string ModelType,
    string ContentType,
    byte[] Payload);

/// <summary>
/// Lightweight in-memory queue that buffers messages arriving from the engine
/// until the rest of the harness is ready to process them.
/// </summary>
internal sealed class SignalsIntelligenceInbox
{
    private readonly Channel<SignalsIntelligenceMessage> _channel = Channel.CreateUnbounded<SignalsIntelligenceMessage>(
        new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = false,
            SingleWriter = true
        });

    public ChannelWriter<SignalsIntelligenceMessage> Writer => _channel.Writer;
    public ChannelReader<SignalsIntelligenceMessage> Reader => _channel.Reader;
}
