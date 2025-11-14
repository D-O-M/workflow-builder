using Backend.Plugins.Communication.RingBuffers;

namespace Backend.Plugins.Communication;

/// <summary>
/// Wraps two ring buffers (one per direction) to expose a duplex channel.
/// </summary>
public sealed class MemoryMappedNanoServiceChannel : INanoServiceChannel
{
    private readonly MemoryMappedRingBuffer _outbound;
    private readonly MemoryMappedRingBuffer _inbound;
    private bool _disposed;

    public MemoryMappedNanoServiceChannel(MemoryMappedRingBuffer outbound, MemoryMappedRingBuffer inbound)
    {
        _outbound = outbound;
        _inbound = inbound;
    }

    public bool TrySend(in NanoServiceMessage message)
    {
        EnsureNotDisposed();
        return _outbound.TryWrite(message.Command, message.Payload.Span);
    }

    public bool TryReceive(out NanoServiceMessage message)
    {
        EnsureNotDisposed();
        return _inbound.TryRead(out message);
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _outbound.Dispose();
        _inbound.Dispose();
    }
}
