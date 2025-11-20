using Backend.Plugins.Communication;
using Backend.Plugins.Communication.Protocols.Invocation;

namespace Backend.Engine.ProtocolHandlers;

internal sealed class InvocationInitiator : IInvocationInitiator
{
    private readonly ISignalChannel _channel;
    private readonly SignalAction _globalState;
    private readonly SignalAction _invocationInput;
    private readonly CancellationToken _cancellationToken;

    public InvocationInitiator(
        ISignalChannel channel,
        SignalAction globalState,
        SignalAction invocationInput,
        CancellationToken cancellationToken)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _globalState = globalState ?? throw new ArgumentNullException(nameof(globalState));
        _invocationInput = invocationInput ?? throw new ArgumentNullException(nameof(invocationInput));
        _cancellationToken = cancellationToken;
    }

    public Task SendSignalViaRingBuffer(InvocationSignal signal)
        => SendSignalAsync(signal);

    public Task AwaitResponseFromResponderViaRingBuffer(InvocationSignal signal)
        => AwaitSignalAsync(signal);

    public Task SendGlobalState()
        => SendPayloadAsync(InvocationSignal.GlobalState, _globalState);

    public Task AwaitGlobalStateAcknowledgement()
        => AwaitSignalAsync(InvocationSignal.GlobalStateAcknowledged);

    public Task SendInvocationInput()
        => SendPayloadAsync(InvocationSignal.InvocationInput, _invocationInput);

    public Task AwaitInvocationInputAcknowledgement()
        => AwaitSignalAsync(InvocationSignal.InvocationInputAcknowledged);

    private async Task SendPayloadAsync(InvocationSignal signal, SignalAction action)
    {
        var payload = SignalActionSerializer.Serialize(action);
        var envelope = new SignalWithPayloadEnvelope<InvocationSignal>(signal, payload.ToArray());
        await SendAsync(envelope.ToBytes());
    }

    private async Task SendSignalAsync(InvocationSignal signal)
    {
        var envelope = new SignalEnvelope<InvocationSignal>(signal).ToBytes();
        await SendAsync(envelope);
    }

    private async Task AwaitSignalAsync(InvocationSignal expectedSignal)
    {
        while (!_cancellationToken.IsCancellationRequested)
        {
            if (_channel.TryReceive(out var message))
            {
                if (message.Command is not SignalCommand.Dispatch)
                {
                    throw new InvalidOperationException($"Unexpected command {message.Command} encountered during invocation.");
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
