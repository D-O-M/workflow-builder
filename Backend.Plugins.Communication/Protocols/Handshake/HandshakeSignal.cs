namespace Backend.Plugins.Communication.Protocols.Handshake;

public enum HandshakeSignal : byte
{
    Hello,
    Acknowledge,
    Setup,
    AcknowledgeWithReceipt,
    Ready,
    Data,
    UpgradeCommsMethod,
    ReadyForTest,
    Test
}
