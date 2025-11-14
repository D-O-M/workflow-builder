using System;
using System.Threading.Tasks;

namespace Backend.Plugins.Communication;

/// <summary>
/// Represents an active IPC connection between the engine and a nano service.
/// Disposing the connection tears down the underlying channel.
/// </summary>
public sealed record SignalConnection(NanoServiceHandshake Handshake, INanoServiceChannel Channel) : IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        Channel.Dispose();
        return ValueTask.CompletedTask;
    }
}
