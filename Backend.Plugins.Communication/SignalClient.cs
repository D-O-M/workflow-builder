using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Backend.Plugins.Communication;

/// <summary>
/// Handles the responder side of the IPC negotiation initiated over STDIO.
/// </summary>
public sealed class SignalClient
{
    private readonly TextReader _handshakeReader;
    private readonly TextWriter _handshakeWriter;

    public SignalClient()
        : this(Console.In, Console.Out)
    {
    }

    public SignalClient(TextReader handshakeReader, TextWriter handshakeWriter)
    {
        _handshakeReader = handshakeReader ?? throw new ArgumentNullException(nameof(handshakeReader));
        _handshakeWriter = handshakeWriter ?? throw new ArgumentNullException(nameof(handshakeWriter));
    }

    /// <summary>
    /// Waits for the handshake, acknowledges it, and returns a ready-to-use
    /// connection bound to the shared memory channel.
    /// </summary>
    public async Task<SignalConnection> EstablishAsync(CancellationToken cancellationToken)
    {
        var payloadTask = _handshakeReader.ReadLineAsync();
        var payload = await payloadTask.WaitAsync(cancellationToken)
            ?? throw new InvalidOperationException("Signal handshake payload was not received.");

        var handshake = SignalHandshakeSerializer.Deserialize(payload);

        var ackLine = $"{SignalProtocolConstants.HandshakeAcknowledgementPrefix}:{handshake.SessionId:N}";
        await _handshakeWriter.WriteLineAsync(ackLine).WaitAsync(cancellationToken);
        await _handshakeWriter.FlushAsync().WaitAsync(cancellationToken);

        var channel = SignalProtocolFactory.CreateChannel(handshake, SignalEndpointRole.Responder);
        return new SignalConnection(handshake, channel);
    }
}
