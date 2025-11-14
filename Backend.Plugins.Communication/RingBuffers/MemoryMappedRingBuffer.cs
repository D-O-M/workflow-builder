using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace Backend.Plugins.Communication.RingBuffers;

/// <summary>
/// Implements a single-producer/single-consumer ring buffer backed by a
/// memory-mapped file. The structure is OS-independent and can be shared across
/// processes.
/// </summary>
public sealed class MemoryMappedRingBuffer : IDisposable
{
    private const int HeaderSize = sizeof(int) * 2; // head + tail
    private const int MessageHeaderSize = 1 + sizeof(int); // command + payload length

    private readonly int _capacity;
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly Mutex _mutex;
    private bool _disposed;

    public MemoryMappedRingBuffer(string mapName, string mutexName, int capacity)
    {
        if (capacity < 256)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Ring buffers must be at least 256 bytes.");
        }

        _capacity = AlignCapacity(capacity);
        _mmf = MemoryMappedFile.CreateOrOpen(mapName, HeaderSize + _capacity);
        _accessor = _mmf.CreateViewAccessor(0, HeaderSize + _capacity, MemoryMappedFileAccess.ReadWrite);
        _mutex = new Mutex(false, mutexName);
    }

    public bool TryWrite(NanoServiceCommand command, ReadOnlySpan<byte> payload)
    {
        EnsureNotDisposed();
        var messageLength = payload.Length + MessageHeaderSize;

        if (messageLength >= _capacity)
        {
            throw new InvalidOperationException("Payload cannot be larger than the configured ring buffer capacity.");
        }

        using var releaser = new CrossProcessMutexGuard(_mutex);
        var head = ReadHead();
        var tail = ReadTail();

        if (FreeSpace(head, tail) <= messageLength)
        {
            return false; // no space, let the caller retry.
        }

        var writeIndex = head;

        Span<byte> header = stackalloc byte[MessageHeaderSize];
        header[0] = (byte)command;
        var payloadLength = payload.Length;
        MemoryMarshal.Write(header[1..], ref payloadLength);

        WriteToRing(writeIndex, header);
        writeIndex = Advance(writeIndex, header.Length);
        WriteToRing(writeIndex, payload);
        head = Advance(head, messageLength);
        WriteHead(head);
        return true;
    }

    public bool TryRead(out NanoServiceMessage message)
    {
        EnsureNotDisposed();
        using var releaser = new CrossProcessMutexGuard(_mutex);
        var head = ReadHead();
        var tail = ReadTail();

        if (head == tail)
        {
            message = NanoServiceMessage.Empty();
            return false;
        }

        Span<byte> header = stackalloc byte[MessageHeaderSize];
        ReadFromRing(tail, header);
        var command = (NanoServiceCommand)header[0];
        var payloadLength = MemoryMarshal.Read<int>(header[1..]);
        tail = Advance(tail, header.Length);

        if (payloadLength < 0 || payloadLength >= _capacity)
        {
            throw new InvalidOperationException("Corrupted payload length detected while reading from the ring buffer.");
        }

        var payload = payloadLength == 0
            ? Array.Empty<byte>()
            : GC.AllocateUninitializedArray<byte>(payloadLength);

        if (payloadLength > 0)
        {
            ReadFromRing(tail, payload);
            tail = Advance(tail, payloadLength);
        }

        WriteTail(tail);

        message = new NanoServiceMessage(command, payload);
        return true;
    }

    private static int AlignCapacity(int value)
    {
        const int alignment = 64;
        var remainder = value % alignment;
        return remainder == 0 ? value : value + (alignment - remainder);
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private int ReadHead() => _accessor.ReadInt32(0);

    private void WriteHead(int value) => _accessor.Write(0, value);

    private int ReadTail() => _accessor.ReadInt32(sizeof(int));

    private void WriteTail(int value) => _accessor.Write(sizeof(int), value);

    private int Advance(int index, int count)
    {
        var next = index + count;
        if (next >= _capacity)
        {
            next -= _capacity;
        }

        return next;
    }

    private int FreeSpace(int head, int tail)
    {
        if (head >= tail)
        {
            return _capacity - (head - tail) - 1;
        }

        return tail - head - 1;
    }

    private void WriteToRing(int index, ReadOnlySpan<byte> source)
    {
        var absoluteIndex = HeaderSize + index;
        var remaining = source.Length;
        var offset = 0;

        while (remaining > 0)
        {
            var contiguous = Math.Min(remaining, _capacity - index);
            var chunk = ArrayPool<byte>.Shared.Rent(contiguous);
            try
            {
                var chunkSpan = chunk.AsSpan(0, contiguous);
                source[offset..(offset + contiguous)].CopyTo(chunkSpan);
                _accessor.WriteArray(absoluteIndex, chunk, 0, contiguous);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }

            remaining -= contiguous;
            offset += contiguous;
            index = Advance(index, contiguous);
            absoluteIndex = HeaderSize + index;
        }
    }

    private void ReadFromRing(int index, Span<byte> destination)
    {
        var absoluteIndex = HeaderSize + index;
        var remaining = destination.Length;
        var offset = 0;

        while (remaining > 0)
        {
            var contiguous = Math.Min(remaining, _capacity - index);
            var buffer = ArrayPool<byte>.Shared.Rent(contiguous);
            try
            {
                _accessor.ReadArray(absoluteIndex, buffer, 0, contiguous);
                buffer.AsSpan(0, contiguous).CopyTo(destination[offset..]);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            remaining -= contiguous;
            offset += contiguous;
            index = Advance(index, contiguous);
            absoluteIndex = HeaderSize + index;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _accessor.Dispose();
        _mmf.Dispose();
        _mutex.Dispose();
    }

    private readonly struct CrossProcessMutexGuard : IDisposable
    {
        private readonly Mutex _mutex;

        public CrossProcessMutexGuard(Mutex mutex)
        {
            _mutex = mutex;
            try
            {
                _mutex.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                // The other side crashed. We can continue because the state is still consistent.
            }
        }

        public void Dispose() => _mutex.ReleaseMutex();
    }
}
