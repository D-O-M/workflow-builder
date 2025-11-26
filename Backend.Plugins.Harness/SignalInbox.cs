using System.Threading.Channels;
using Backend.Plugins.Communication;

namespace Backend.Plugins.Harness;

internal enum SignalsMessageKind
{
    InputChannelModel,
    OutputChannelModel,
    GlobalState,
    Invocation
}

internal sealed record Signal(
    SignalsMessageKind Kind,
    string Identifier,
    string ModelType,
    string ContentType,
    byte[] Payload,
    InvocationEnvelope? Invocation = null);

/// <summary>
/// Lightweight in-memory queue that buffers messages arriving from the engine
/// until the rest of the harness is ready to process them.
/// </summary>
internal sealed class SignalInbox
{
    private readonly Channel<Signal> _channel = Channel.CreateUnbounded<Signal>(
        new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = false,
            SingleWriter = true
        });

    public ChannelWriter<Signal> Writer => _channel.Writer;
    public ChannelReader<Signal> Reader => _channel.Reader;
}
