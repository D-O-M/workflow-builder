namespace Backend.Plugins.Communication;

/// <summary>
/// Handles the initiator side of the IPC negotiation and provides a
/// ready-to-use channel back to callers.
/// </summary>
public sealed class SignalBroadcaster
{
    private readonly TextWriter _handshakeWriter;
    private readonly TextReader _handshakeReader;

    public SignalBroadcaster(TextWriter handshakeWriter, TextReader handshakeReader)
    {
        _handshakeWriter = handshakeWriter ?? throw new ArgumentNullException(nameof(handshakeWriter));
        _handshakeReader = handshakeReader ?? throw new ArgumentNullException(nameof(handshakeReader));
    }

    public async Task<SignalConnection> EstablishAsync(SignalHandshake handshake, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handshake);

        var payload = SignalHandshakeSerializer.Serialize(handshake);
        await _handshakeWriter.WriteLineAsync(payload).WaitAsync(cancellationToken);
        await _handshakeWriter.FlushAsync().WaitAsync(cancellationToken);

        var expectedAck = $"{SignalProtocolConstants.HandshakeAcknowledgementPrefix}:{handshake.SessionId:N}";
        var ack = await _handshakeReader.ReadLineAsync().WaitAsync(cancellationToken);
        if (!string.Equals(ack, expectedAck, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unexpected signal handshake acknowledgement. Expected '{expectedAck}' but received '{ack ?? "<null>"}'.");
        }

        var channel = SignalProtocolFactory.CreateChannel(handshake, SignalEndpointRole.Initiator);
        return new SignalConnection(handshake, channel);
    }
}
