using System.Text.Json;
using Backend.Plugins.Communication;
using Backend.Plugins.Communication.Protocols;
using Backend.Plugins.Communication.Protocols.Handshake;

namespace Backend.Engine;

internal sealed class HandshakeInitiator : IHandshakeInitiator
{
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private readonly CancellationToken _cancellationToken;
    private readonly byte[] _configurationBytes;
    private readonly byte[] _mmfBytes;
    private readonly EnhancedCommunicationConfiguration _handshake;
    private ISignalChannel? _signalChannel;

    public SignalConnection? Connection { get; private set; }

    public HandshakeInitiator(
        EnhancedCommunicationConfiguration handshake,
        byte[] configurationBytes,
        Stream stdin,
        Stream stdout,
        CancellationToken cancellationToken)
    {
        _handshake = handshake ?? throw new ArgumentNullException(nameof(handshake));
        _configurationBytes = configurationBytes ?? throw new ArgumentNullException(nameof(configurationBytes));
        _mmfBytes = JsonSerializer.SerializeToUtf8Bytes(handshake);
        _stdin = stdin;
        _stdout = stdout;
        _cancellationToken = cancellationToken;
    }

    public async Task SendSignalViaStandardIn(HandshakeSignal signal)
    {
        var envelope = new SignalEnvelope<HandshakeSignal>(signal).ToBytes();
        await _stdin.WriteAsync(envelope, _cancellationToken);
        await _stdin.FlushAsync(_cancellationToken);
    }

    public Task SendSignalViaRingBuffer(HandshakeSignal signal) => SendSignalOverRingBuffer(signal);

    public async Task SendConfig()
    {
        var envelope = new SignalWithPayloadEnvelope<HandshakeSignal>(
            HandshakeSignal.Data,
            _configurationBytes);

        await _stdin.WriteAsync(envelope.ToBytes(), _cancellationToken);
        await _stdin.FlushAsync(_cancellationToken);
    }

    public async Task SendMmfFileHandles()
    {
        var envelope = new SignalWithPayloadEnvelope<HandshakeSignal>(
            HandshakeSignal.Data,
            _mmfBytes);

        await _stdin.WriteAsync(envelope.ToBytes(), _cancellationToken);
        await _stdin.FlushAsync(_cancellationToken);
    }

    public Task SendTestViaRingBuffer() => SendSignalOverRingBuffer(HandshakeSignal.Test);

    public Task SendTestReceiptIfSuccessful() => SendSignalOverRingBuffer(HandshakeSignal.AcknowledgeWithReceipt);

    public Task ReceiveTestViaRingBuffer() => AwaitSignalFromRingBuffer(HandshakeSignal.Test);

    public Task ReceiveAndVerifyTestReceipt() => AwaitSignalFromRingBuffer(HandshakeSignal.AcknowledgeWithReceipt);

    public async Task ReceiveAndVerifyConfigChecksum()
    {
        var envelope = await ReadSignalWithPayloadEnvelopeAsync();

        if (envelope.Signal is not HandshakeSignal.AcknowledgeWithReceipt)
        {
            throw new ArgumentOutOfRangeException(nameof(envelope.Signal));
        }

        var expectedChecksum = _configurationBytes.ComputeXorChecksum();
        if (envelope.Payload is null || envelope.Payload.Length != 1 || envelope.Payload[0] != expectedChecksum)
        {
            throw new InvalidOperationException("Configuration checksum from acceptor did not match.");
        }
    }

    public async Task ReceiveAndVerifyMmfFileHandlesChecksum()
    {
        var envelope = await ReadSignalWithPayloadEnvelopeAsync();

        if (envelope.Signal is not HandshakeSignal.AcknowledgeWithReceipt)
        {
            throw new ArgumentOutOfRangeException(nameof(envelope.Signal));
        }

        var expectedChecksum = _mmfBytes.ComputeXorChecksum();
        if (envelope.Payload is null || envelope.Payload.Length != 1 || envelope.Payload[0] != expectedChecksum)
        {
            throw new InvalidOperationException("MMF handle checksum from acceptor did not match.");
        }

        _signalChannel = SignalProtocolFactory.CreateChannel(_handshake, SignalEndpointRole.Initiator);
        Connection = new SignalConnection(_handshake, _signalChannel);
    }

    public async Task AwaitResponseFromResponderViaStandardOut(HandshakeSignal signal)
    {
        var envelope = await ReadSignalEnvelopeAsync();
        if (envelope.Signal != signal)
        {
            throw new ArgumentOutOfRangeException(nameof(envelope.Signal));
        }
    }

    public Task AwaitResponseFromResponderViaRingBuffer(HandshakeSignal signal) => AwaitSignalFromRingBuffer(signal);

    private async Task SendSignalOverRingBuffer(HandshakeSignal signal)
    {
        EnsureSignalChannel();
        var envelope = new SignalEnvelope<HandshakeSignal>(signal).ToBytes();
        var message = new SignalMessage(SignalCommand.Dispatch, envelope);

        while (!_signalChannel!.TrySend(message))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), _cancellationToken);
        }
    }

    private async Task AwaitSignalFromRingBuffer(HandshakeSignal expectedSignal)
    {
        EnsureSignalChannel();

        while (!_cancellationToken.IsCancellationRequested)
        {
            if (_signalChannel!.TryReceive(out var message))
            {
                if (message.Command is not SignalCommand.Dispatch)
                {
                    throw new InvalidOperationException($"Unexpected command {message.Command} encountered during handshake.");
                }

                var envelope = SignalEnvelope.FromBytes<HandshakeSignal>(message.Payload.ToArray());
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

    private async Task<SignalEnvelope<HandshakeSignal>> ReadSignalEnvelopeAsync()
    {
        var buffer = new byte[1];
        await _stdout.ReadExactAsync(buffer, _cancellationToken);
        return SignalEnvelope.FromBytes<HandshakeSignal>(buffer);
    }

    private async Task<SignalWithPayloadEnvelope<HandshakeSignal>> ReadSignalWithPayloadEnvelopeAsync()
    {
        var header = new byte[5];
        await _stdout.ReadExactAsync(header, _cancellationToken);

        var length = BitConverter.ToInt32(header, 1);
        if (length < 0)
        {
            throw new InvalidOperationException("Negative payload length received over STDOUT.");
        }

        var payload = length == 0 ? Array.Empty<byte>() : new byte[length];
        if (length > 0)
        {
            await _stdout.ReadExactAsync(payload, _cancellationToken);
        }

        var buffer = new byte[header.Length + payload.Length];
        Buffer.BlockCopy(header, 0, buffer, 0, header.Length);
        if (payload.Length > 0)
        {
            Buffer.BlockCopy(payload, 0, buffer, header.Length, payload.Length);
        }

        return SignalWithPayloadEnvelope.FromBytes<HandshakeSignal>(buffer);
    }

    private void EnsureSignalChannel()
    {
        if (_signalChannel is null)
        {
            throw new InvalidOperationException("Ring buffer channel is not available. Ensure MMF handles were acknowledged.");
        }
    }
}
