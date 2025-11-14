namespace Backend.Plugins.Communication;

/// <summary>
/// Represents a duplex channel between two signal endpoints.
/// </summary>
public interface ISignalChannel : IDisposable
{
    /// <summary>
    /// Attempts to enqueue a message for the counterpart.
    /// </summary>
    bool TrySend(in SignalMessage message);

    /// <summary>
    /// Attempts to dequeue the next available message.
    /// </summary>
    bool TryReceive(out SignalMessage message);
}
