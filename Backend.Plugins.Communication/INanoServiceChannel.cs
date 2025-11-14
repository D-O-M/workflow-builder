namespace Backend.Plugins.Communication;

/// <summary>
/// Represents a duplex channel between the engine and a nano service.
/// </summary>
public interface INanoServiceChannel : IDisposable
{
    /// <summary>
    /// Attempts to enqueue a message for the other side.
    /// </summary>
    bool TrySend(in NanoServiceMessage message);

    /// <summary>
    /// Attempts to dequeue the next available message.
    /// </summary>
    bool TryReceive(out NanoServiceMessage message);
}
