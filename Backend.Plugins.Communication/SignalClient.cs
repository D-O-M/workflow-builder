using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Backend.Plugins.Communication;

/// <summary>
/// Handles the nano-service side of the IPC negotiation initiated by the engine.
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
    /// Waits for the engine handshake over STDIN/STDOUT, acknowledges it, and
    /// returns a ready-to-use connection bound to the shared memory channel.
    /// </summary>
    public async Task<SignalConnection> EstablishAsync(CancellationToken cancellationToken)
    {
        var payloadTask = _handshakeReader.ReadLineAsync();
        var payload = await payloadTask.WaitAsync(cancellationToken)
            ?? throw new InvalidOperationException("Engine handshake payload was not received.");

        var handshake = NanoServiceHandshakeSerializer.Deserialize(payload);

        var ackLine = $"{NanoServiceProtocolConstants.HandshakeAcknowledgementPrefix}:{handshake.SessionId:N}";
        await _handshakeWriter.WriteLineAsync(ackLine).WaitAsync(cancellationToken);
        await _handshakeWriter.FlushAsync().WaitAsync(cancellationToken);

        var channel = NanoServiceProtocolFactory.CreateNanoServiceChannel(handshake);
        return new SignalConnection(handshake, channel);
    }
}
