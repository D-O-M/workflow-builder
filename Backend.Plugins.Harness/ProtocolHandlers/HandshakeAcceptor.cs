using System.Text.Json;
using Backend.Plugins.Communication;
using Backend.Plugins.Communication.Protocols;
using Backend.Plugins.Communication.Protocols.Handshake;
using Backend.Plugins.Communication.RingBuffers;

namespace Backend.Plugins.Harness.ProtocolHandlers;

internal class HandshakeAcceptor : IHandshakeAcceptor
{
    private byte[]? _configBytes = null;
    private byte[]? _mmfFileHandleBytes = null;
    private EnhancedCommunicationConfiguration? _handshake;
    private ISignalChannel? _signalChannel = null;
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private readonly CancellationToken _cancellationToken;

    public SignalConnection? Connection { get; private set; }

    public HandshakeAcceptor(
        Stream stdin,
        Stream stdout,
        CancellationToken cancellationToken)
    {
        _stdin = stdin;
        _stdout = stdout;
        _cancellationToken = cancellationToken;
    }

    public async Task AwaitMessageFromInitiatorViaStandardIn(HandshakeSignal signal)
    {
        var envelope = await ReadSignalEnvelopeAsync();

        if (envelope.Signal != signal)
            throw new ArgumentOutOfRangeException(nameof(envelope.Signal));
    }

    public async Task AwaitMessageFromInitiatorViaRingBuffer(HandshakeSignal signal)
    {
        await AwaitSignalViaRingBuffer(signal);
    }

    public async Task SendSignalViaStandardOut(HandshakeSignal signal)
    {
        var envelope = new SignalEnvelope<HandshakeSignal>(signal).ToBytes();
        await _stdout.WriteAsync(envelope, _cancellationToken);
        await _stdout.FlushAsync(_cancellationToken);
    }

    public async Task SendSignalViaRingBuffer(HandshakeSignal signal)
    {
        await SendSignalOverRingBuffer(signal);
    }

    public async Task ReceiveConfig()
    {
        var envelope = await ReadSignalWithPayloadEnvelopeAsync();

        if (envelope.Signal is not HandshakeSignal.Data)
            throw new ArgumentOutOfRangeException(nameof(envelope.Signal));

        _configBytes = envelope.Payload;
    }

    public async Task SendChecksumForConfig()
    {
        ArgumentNullException.ThrowIfNull(_configBytes, nameof(_configBytes));

        var checksum = _configBytes.ComputeXorChecksum();
        var envelope = new SignalWithPayloadEnvelope<HandshakeSignal>(
            HandshakeSignal.AcknowledgeWithReceipt, [checksum]);

        await _stdout.WriteAsync(envelope.ToBytes(), _cancellationToken);
        await _stdout.FlushAsync(_cancellationToken);
    }

    public async Task ReceiveMmfFileHandles()
    {
        var envelope = await ReadSignalWithPayloadEnvelopeAsync();

        if (envelope.Signal is not HandshakeSignal.Data)
            throw new ArgumentOutOfRangeException(nameof(envelope.Signal));

        _mmfFileHandleBytes = envelope.Payload;
    }

    public async Task SendMmfFileHandlesChecksum()
    {
        ArgumentNullException.ThrowIfNull(
            _mmfFileHandleBytes,
            nameof(_mmfFileHandleBytes));

        var checksum = _mmfFileHandleBytes.ComputeXorChecksum();
        var envelope = new SignalWithPayloadEnvelope<HandshakeSignal>(
            HandshakeSignal.AcknowledgeWithReceipt, [checksum]);

        await _stdout.WriteAsync(envelope.ToBytes(), _cancellationToken);
        await _stdout.FlushAsync(_cancellationToken);

        _handshake = await JsonSerializer
            .DeserializeAsync<EnhancedCommunicationConfiguration>(
                new MemoryStream(_mmfFileHandleBytes));

        _signalChannel = CreateChannel(_handshake!);
        Connection = new SignalConnection(_handshake!, _signalChannel);
    }

    public Task SendTestViaRingBuffer() => SendSignalOverRingBuffer(HandshakeSignal.Test);

    public Task SendTestReceiptIfSuccessful() => SendSignalOverRingBuffer(HandshakeSignal.AcknowledgeWithReceipt);

    public Task ReceiveTestViaRingBuffer() => AwaitSignalViaRingBuffer(HandshakeSignal.Test);

    public Task ReceiveAndVerifyTestReceipt() => AwaitSignalViaRingBuffer(HandshakeSignal.AcknowledgeWithReceipt);

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

    private async Task AwaitSignalViaRingBuffer(HandshakeSignal expectedSignal)
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

    private void EnsureSignalChannel()
    {
        if (_signalChannel is null)
        {
            throw new InvalidOperationException("Ring buffer channel is not available. Ensure MMF handles were received.");
        }
    }

    private async Task<SignalEnvelope<HandshakeSignal>> ReadSignalEnvelopeAsync()
    {
        var buffer = new byte[1];
        await _stdin.ReadExactAsync(buffer, _cancellationToken);
        return SignalEnvelope.FromBytes<HandshakeSignal>(buffer);
    }

    private async Task<SignalWithPayloadEnvelope<HandshakeSignal>> ReadSignalWithPayloadEnvelopeAsync()
    {
        var header = new byte[5];
        await _stdin.ReadExactAsync(header, _cancellationToken);

        var length = BitConverter.ToInt32(header, 1);
        if (length < 0)
        {
            throw new InvalidOperationException("Negative payload length received over STDIN.");
        }

        var payload = length == 0 ? Array.Empty<byte>() : new byte[length];
        if (length > 0)
        {
            await _stdin.ReadExactAsync(payload, _cancellationToken);
        }

        var buffer = new byte[header.Length + payload.Length];
        Buffer.BlockCopy(header, 0, buffer, 0, header.Length);
        if (payload.Length > 0)
        {
            Buffer.BlockCopy(payload, 0, buffer, header.Length, payload.Length);
        }

        return SignalWithPayloadEnvelope.FromBytes<HandshakeSignal>(buffer);
    }

    public static ISignalChannel CreateChannel(
        EnhancedCommunicationConfiguration handshake)
    {
        ArgumentNullException.ThrowIfNull(handshake);

        var outbound = new MemoryMappedRingBuffer(
                handshake.AcceptorToInitiatorMapName,
                handshake.AcceptorToInitiatorMutexName,
                handshake.AcceptorToInitiatorEventName,
                handshake.AcceptorToInitiatorCapacity);

        var inbound = new MemoryMappedRingBuffer(
                handshake.InitiatorToAcceptorMapName,
                handshake.InitiatorToAcceptorMutexName,
                handshake.InitiatorToAcceptorEventName,
                handshake.InitiatorToAcceptorCapacity);

        return new MemoryMappedSignalChannel(outbound, inbound);
    }
}
