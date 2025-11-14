using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Backend.Plugins.Communication;

/// <summary>
/// Handles the engine side of the IPC negotiation and provides a ready-to-use
/// channel back to callers.
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

    public async Task<SignalConnection> EstablishAsync(NanoServiceHandshake handshake, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handshake);

        var payload = NanoServiceHandshakeSerializer.Serialize(handshake);
        await _handshakeWriter.WriteLineAsync(payload).WaitAsync(cancellationToken);
        await _handshakeWriter.FlushAsync().WaitAsync(cancellationToken);

        var expectedAck = $"{NanoServiceProtocolConstants.HandshakeAcknowledgementPrefix}:{handshake.SessionId:N}";
        var ack = await _handshakeReader.ReadLineAsync().WaitAsync(cancellationToken);
        if (!string.Equals(ack, expectedAck, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unexpected nano service handshake acknowledgement. Expected '{expectedAck}' but received '{ack ?? "<null>"}'.");
        }

        var channel = NanoServiceProtocolFactory.CreateEngineChannel(handshake);
        return new SignalConnection(handshake, channel);
    }
}
