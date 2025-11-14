namespace Backend.Plugins.Communication;

/// <summary>
/// Shared protocol constants understood by both the engine and nano services.
/// </summary>
public static class NanoServiceProtocolConstants
{
    /// <summary>
    /// Prefix used by the nano service harness when acknowledging the initial
    /// handshake over STDIO. The remainder of the line contains the session id
    /// in "N" format.
    /// </summary>
    public const string HandshakeAcknowledgementPrefix = "NS-ACK";
}
