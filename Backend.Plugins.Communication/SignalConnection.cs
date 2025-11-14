using System.Threading.Tasks;

namespace Backend.Plugins.Communication;

/// <summary>
/// Represents an active IPC connection between two signal endpoints. Disposing
/// the connection tears down the underlying channel.
/// </summary>
public sealed record SignalConnection(SignalHandshake Handshake, ISignalChannel Channel) : IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        Channel.Dispose();
        return ValueTask.CompletedTask;
    }
}
