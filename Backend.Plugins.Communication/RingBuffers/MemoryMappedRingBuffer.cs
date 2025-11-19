using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace Backend.Plugins.Communication.RingBuffers;

internal static class WaitHandleExtensions
{
    /// <summary>
    /// Asynchronously waits for the specified wait handle to be signaled.
    /// Uses ThreadPool.RegisterWaitForSingleObject under the hood.
    /// </summary>
    public static Task WaitOneAsync(
        this WaitHandle handle,
        CancellationToken cancellationToken = default)
    {
        if (handle == null) throw new ArgumentNullException(nameof(handle));

        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        RegisteredWaitHandle? registration = null;

        registration = ThreadPool.RegisterWaitForSingleObject(
            handle,
            static (state, timedOut) =>
            {
                var (innerTcs, innerRegistration) =
                    ((TaskCompletionSource<bool>, RegisteredWaitHandle?))state!;

                innerRegistration?.Unregister(null);

                // We don't actually care about timedOut here because we use infinite timeout.
                innerTcs.TrySetResult(!timedOut);
            },
            (tcs, registration),
            Timeout.Infinite,
            executeOnlyOnce: true);

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() =>
            {
                registration?.Unregister(null);
                tcs.TrySetCanceled(cancellationToken);
            });
        }

        return tcs.Task;
    }
}

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
    private readonly EventWaitHandle _dataAvailableEvent;

    private bool _disposed;

    /// <summary>
    /// Creates or opens a cross-process ring buffer backed by a memory-mapped file.
    /// Uses a file-backed mapping in the system temp directory; mapName is used as the file name.
    /// </summary>
    /// <param name="mapName">Logical name for the mapping (and backing file name in the temp directory).</param>
    /// <param name="mutexName">Named cross-process mutex to protect head/tail and buffer operations.</param>
    /// <param name="dataAvailableEventName">Named event used to signal that data is available for reading.</param>
    /// <param name="capacity">Ring buffer capacity in bytes (will be aligned up to 64-byte boundary).</param>
    public MemoryMappedRingBuffer(
        string mapName,
        string mutexName,
        string dataAvailableEventName,
        int capacity)
    {
        if (capacity < 256)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Ring buffers must be at least 256 bytes.");
        }

        _capacity = AlignCapacity(capacity);
        var totalSize = HeaderSize + _capacity;

        // Single cross-platform implementation: file-backed mapping in temp directory.
        var backingFilePath = Path.Combine(Path.GetTempPath(), mapName);

        Directory.CreateDirectory(Path.GetDirectoryName(backingFilePath)!);

        var fileStream = new FileStream(
            backingFilePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite);

        if (fileStream.Length < totalSize)
        {
            fileStream.SetLength(totalSize);
        }

        _mmf = MemoryMappedFile.CreateFromFile(
            fileStream,
            mapName: null, // name not used cross-process for file-backed mapping
            capacity: totalSize,
            access: MemoryMappedFileAccess.ReadWrite,
            inheritability: HandleInheritability.None,
            leaveOpen: false);

        _accessor = _mmf.CreateViewAccessor(0, totalSize, MemoryMappedFileAccess.ReadWrite);
        _mutex = new Mutex(false, mutexName);

        // AutoReset ensures only one waiter is released per Set()
        _dataAvailableEvent = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.AutoReset,
            name: dataAvailableEventName);
    }

    /// <summary>
    /// Attempts to write a message into the ring buffer.
    /// Returns false if there is not enough space; caller may retry later.
    /// </summary>
    public bool TryWrite(SignalCommand command, ReadOnlySpan<byte> payload)
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
        MemoryMarshal.Write(header[1..], in payloadLength);

        WriteToRing(writeIndex, header);
        writeIndex = Advance(writeIndex, header.Length);
        WriteToRing(writeIndex, payload);

        head = Advance(head, messageLength);
        WriteHead(head);

        // Signal that data is available for any waiting readers (in this or another process).
        _dataAvailableEvent.Set();

        return true;
    }

    /// <summary>
    /// Attempts to read a single message from the ring buffer.
    /// Returns false if the buffer is currently empty.
    /// </summary>
    public bool TryRead(out SignalMessage message)
    {
        EnsureNotDisposed();

        using var releaser = new CrossProcessMutexGuard(_mutex);
        var head = ReadHead();
        var tail = ReadTail();

        if (head == tail)
        {
            message = SignalMessage.Empty();
            return false;
        }

        Span<byte> header = stackalloc byte[MessageHeaderSize];
        ReadFromRing(tail, header);

        var command = (SignalCommand)header[0];
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

        message = new SignalMessage(command, payload);
        return true;
    }

    /// <summary>
    /// Asynchronously waits for and reads a single message from the ring buffer.
    /// This will block (asynchronously) until data is available or cancellation is requested.
    /// </summary>
    public async Task<SignalMessage> ReadAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (TryRead(out var message))
            {
                return message;
            }

            // Wait to be signalled that data is available; loop will TryRead again.
            await _dataAvailableEvent.WaitOneAsync(cancellationToken);
        }
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
        _dataAvailableEvent.Dispose();
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
