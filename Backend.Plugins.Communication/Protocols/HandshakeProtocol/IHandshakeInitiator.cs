namespace Backend.Plugins.Communication.Protocols.HandshakeProtocol;

public interface IInitiator
{
    Task AwaitResponseFromResponderViaStandardOut();
    Task AwaitResponseFromResponderViaRingBuffer();
}

public interface IHandshakeInitiator : IInitiator
{
    Task SendSignalViaStandardIn(HandshakeSignal signal);
    Task SendSignalViaRingBuffer(HandshakeSignal signal);
    Task SendConfig();
    Task SendMmfFileHandles();
    Task SendTestViaRingBuffer();
    Task SendTestReceiptIfSuccessful();
}
