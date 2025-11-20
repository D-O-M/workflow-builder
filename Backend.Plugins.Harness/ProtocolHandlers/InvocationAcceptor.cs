using Backend.Plugins.Communication;
using Backend.Plugins.Communication.Protocols.Invocation;

namespace Backend.Plugins.Harness.ProtocolHandlers;

internal sealed class InvocationAcceptor : IInvocationAcceptor
{
    private readonly ISignalChannel _channel;
    private readonly CancellationToken _cancellationToken;

    public InvocationAcceptor(ISignalChannel channel, CancellationToken cancellationToken)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _cancellationToken = cancellationToken;
    }

    public SignalAction? GlobalState { get; private set; }
    public SignalAction? InvocationInput { get; private set; }

    public Task AwaitMessageFromInitiatorViaRingBuffer(InvocationSignal signal)
        => AwaitSignalAsync(signal);

    public Task SendSignalViaRingBuffer(InvocationSignal signal)
        => SendSignalAsync(signal);

    public async Task ReceiveGlobalState()
    {
        var envelope = await AwaitPayloadEnvelopeAsync(InvocationSignal.GlobalState);
        GlobalState = SignalActionSerializer.Deserialize(envelope.Payload);
    }

    public Task SendGlobalStateAcknowledgement()
        => SendSignalAsync(InvocationSignal.GlobalStateAcknowledged);

    public async Task ReceiveInvocationInput()
    {
        var envelope = await AwaitPayloadEnvelopeAsync(InvocationSignal.InvocationInput);
        InvocationInput = SignalActionSerializer.Deserialize(envelope.Payload);
    }

    public Task SendInvocationInputAcknowledgement()
        => SendSignalAsync(InvocationSignal.InvocationInputAcknowledged);

    private async Task SendSignalAsync(InvocationSignal signal)
    {
        var envelope = new SignalEnvelope<InvocationSignal>(signal).ToBytes();
        await SendAsync(envelope);
    }

    private async Task<SignalWithPayloadEnvelope<InvocationSignal>> AwaitPayloadEnvelopeAsync(
        InvocationSignal expectedSignal)
    {
        while (!_cancellationToken.IsCancellationRequested)
        {
            if (_channel.TryReceive(out var message))
            {
                if (message.Command is not SignalCommand.Dispatch)
                {
                    throw new InvalidOperationException($"Unexpected command {message.Command} encountered during invocation receive.");
                }

                var envelope = SignalWithPayloadEnvelope
                    .FromBytes<InvocationSignal>(message.Payload.ToArray());

                if (envelope.Signal != expectedSignal)
                {
                    throw new ArgumentOutOfRangeException(nameof(envelope.Signal));
                }

                return envelope;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5), _cancellationToken);
        }

        _cancellationToken.ThrowIfCancellationRequested();
        throw new OperationCanceledException();
    }

    private async Task AwaitSignalAsync(InvocationSignal expectedSignal)
    {
        while (!_cancellationToken.IsCancellationRequested)
        {
            if (_channel.TryReceive(out var message))
            {
                if (message.Command is not SignalCommand.Dispatch)
                {
                    throw new InvalidOperationException($"Unexpected command {message.Command} encountered during invocation receive.");
                }

                var envelope = SignalEnvelope.FromBytes<InvocationSignal>(message.Payload.ToArray());
                if (envelope.Signal != expectedSignal)
                {
                    throw new ArgumentOutOfRangeException(nameof(envelope.Signal));
                }

                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5), _cancellationToken);
        }

        _cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task SendAsync(ReadOnlyMemory<byte> envelope)
    {
        var message = new SignalMessage(SignalCommand.Dispatch, envelope);

        while (!_cancellationToken.IsCancellationRequested)
        {
            if (_channel.TrySend(message))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5), _cancellationToken);
        }

        _cancellationToken.ThrowIfCancellationRequested();
    }
}
