namespace Backend.Plugins.Communication;

/// <summary>
/// Shared protocol constants understood by both signal endpoints.
/// </summary>
public static class SignalProtocolConstants
{
    /// <summary>
    /// Prefix used when acknowledging the initial handshake over STDIO. The
    /// remainder of the line contains the session id in "N" format.
    /// </summary>
    public const string HandshakeAcknowledgementPrefix = "SIG-ACK";
}
