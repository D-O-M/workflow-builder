using System.Text.Json;
using Backend.Plugins.Communication;
using Backend.Plugins.Communication.Protocols;
using Backend.Plugins.Communication.Protocols.Handshake;
using Backend.Plugins.Communication.RingBuffers;

namespace Backend.Plugins.Harness;

internal class HandshakeAcceptor : IHandshakeAcceptor
{
    private byte[]? _configBytes = null;
    private byte[]? _mmfFileHandleBytes = null;
    private ISignalChannel? _signalChannel = null;
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private readonly CancellationToken _cancellationToken;

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
        var bytes = await ReadAllBytesFromStandardIn() ?? throw new Exception();
        var envelope = SignalEnvelope.FromBytes<HandshakeSignal>(bytes);

        if (envelope.Signal != signal)
            throw new ArgumentOutOfRangeException(nameof(envelope.Signal));
    }

    public Task AwaitMessageFromInitiatorViaRingBuffer(HandshakeSignal signal)
    {
        throw new NotImplementedException();
    }

    public async Task SendSignalViaStandardOut(HandshakeSignal signal)
    {
        var envelope = new SignalEnvelope<HandshakeSignal>(signal).ToBytes();
        await _stdout.WriteAsync(envelope, _cancellationToken);
        await _stdout.FlushAsync(_cancellationToken);
    }

    public Task SendSignalViaRingBuffer(HandshakeSignal signal)
    {
        throw new NotImplementedException();
    }

    public async Task ReceiveConfig()
    {
        var bytes = await ReadAllBytesFromStandardIn() ?? throw new Exception();
        var envelope = SignalWithPayloadEnvelope
            .FromBytes<HandshakeSignal>(bytes);

        if (envelope.Signal is not HandshakeSignal.Data)
            throw new ArgumentOutOfRangeException(nameof(envelope.Signal));

        _configBytes = envelope.Payload;
    }

    public async Task SendChecksumForConfig()
    {
        ArgumentNullException.ThrowIfNull(_configBytes, nameof(_configBytes));

        var checksum = GetXorChecksum(_configBytes);
        var envelope = new SignalWithPayloadEnvelope<HandshakeSignal>(
            HandshakeSignal.AcknowledgeWithReceipt, [checksum]);

        await _stdout.WriteAsync(envelope.ToBytes(), _cancellationToken);
        await _stdout.FlushAsync(_cancellationToken);
    }

    public async Task ReceiveMmfFileHandles()
    {
        var bytes = await ReadAllBytesFromStandardIn() ?? throw new Exception();
        var envelope = SignalWithPayloadEnvelope
            .FromBytes<HandshakeSignal>(bytes);

        if (envelope.Signal is not HandshakeSignal.Data)
            throw new ArgumentOutOfRangeException(nameof(envelope.Signal));

        _mmfFileHandleBytes = envelope.Payload;
    }

    public async Task SendMmfFileHandlesChecksum()
    {
        ArgumentNullException.ThrowIfNull(
            _mmfFileHandleBytes,
            nameof(_mmfFileHandleBytes));

        var checksum = GetXorChecksum(_mmfFileHandleBytes);
        var envelope = new SignalWithPayloadEnvelope<HandshakeSignal>(
            HandshakeSignal.AcknowledgeWithReceipt, [checksum]);

        await _stdout.WriteAsync(envelope.ToBytes(), _cancellationToken);
        await _stdout.FlushAsync(_cancellationToken);

        var commsConfig = await JsonSerializer
            .DeserializeAsync<EnhancedCommunicationConfiguration>(
                new MemoryStream(_mmfFileHandleBytes));

        _signalChannel = CreateChannel(commsConfig!);
    }

    public Task SendTestViaRingBuffer()
    {

        throw new NotImplementedException();
    }

    public Task SendTestReceiptIfSuccessful()
    {
        throw new NotImplementedException();
    }

    public Task ReceiveTestViaRingBuffer()
    {
        throw new NotImplementedException();
    }

    public Task ReceiveAndVerifyTestReceipt()
    {
        throw new NotImplementedException();
    }

    private async Task<byte[]> ReadAllBytesFromStandardIn()
    {
        using var ms = new MemoryStream();
        await _stdin.CopyToAsync(ms, _cancellationToken);
        return ms.ToArray();
    }

    private static byte GetXorChecksum(byte[] data)
    {
        byte sum = 0;
        foreach (byte b in data)
            sum ^= b;
        return sum;
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
