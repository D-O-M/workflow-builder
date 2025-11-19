namespace Backend.Plugins.Communication.Protocols.HandshakeProtocol;

public enum HandshakeSignal
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
