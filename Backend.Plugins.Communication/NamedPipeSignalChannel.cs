using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Backend.Plugins.Communication;

/// <summary>
/// <see cref="ISignalChannel"/> implementation backed by a pair of named pipes
/// (one for sending, one for receiving).
/// </summary>
public sealed class NamedPipeSignalChannel : ISignalChannel
{
    private const int HeaderLength = sizeof(byte) + sizeof(int);

    private readonly PipeStream _outbound;
    private readonly PipeStream _inbound;
    private readonly ConcurrentQueue<SignalMessage> _inbox = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _reader;
    private readonly object _sendLock = new();

    private bool _disposed;

    private NamedPipeSignalChannel(PipeStream outbound, PipeStream inbound)
    {
        _outbound = outbound;
        _inbound = inbound;
        _reader = Task.Run(ReadLoopAsync, _cts.Token);
    }

    public static async Task<NamedPipeSignalChannel> CreateServerAsync(
        string outboundPipeName,
        string inboundPipeName,
        CancellationToken cancellationToken)
    {
        var outbound = new NamedPipeServerStream(
            outboundPipeName,
            PipeDirection.Out,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);

        var inbound = new NamedPipeServerStream(
            inboundPipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        await Task.WhenAll(
            outbound.WaitForConnectionAsync(cancellationToken),
            inbound.WaitForConnectionAsync(cancellationToken));

        return new NamedPipeSignalChannel(outbound, inbound);
    }

    public static async Task<NamedPipeSignalChannel> CreateClientAsync(
        string outboundPipeName,
        string inboundPipeName,
        CancellationToken cancellationToken)
    {
        var outbound = new NamedPipeClientStream(
            serverName: ".",
            pipeName: inboundPipeName,
            direction: PipeDirection.Out,
            options: PipeOptions.Asynchronous | PipeOptions.WriteThrough);

        var inbound = new NamedPipeClientStream(
            serverName: ".",
            pipeName: outboundPipeName,
            direction: PipeDirection.In,
            options: PipeOptions.Asynchronous);

        await Task.WhenAll(
            outbound.ConnectAsync(cancellationToken),
            inbound.ConnectAsync(cancellationToken));

        return new NamedPipeSignalChannel(outbound, inbound);
    }

    public bool TrySend(in SignalMessage message)
    {
        EnsureNotDisposed();

        var payloadLength = message.Payload.Length;
        var totalLength = HeaderLength + payloadLength;

        var buffer = ArrayPool<byte>.Shared.Rent(totalLength);
        try
        {
            buffer[0] = (byte)message.Command;
            MemoryMarshal.Write(buffer.AsSpan(1, sizeof(int)), in payloadLength);
            message.Payload.Span.CopyTo(buffer.AsSpan(HeaderLength));

            lock (_sendLock)
            {
                _outbound.Write(buffer, 0, totalLength);
                _outbound.Flush();
            }

            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public bool TryReceive(out SignalMessage message)
    {
        EnsureNotDisposed();
        return _inbox.TryDequeue(out message);
    }

    private async Task ReadLoopAsync()
    {
        var header = new byte[HeaderLength];

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (!await ReadExactAsync(_inbound, header, _cts.Token))
                {
                    break;
                }

                var command = (SignalCommand)header[0];
                var payloadLength = MemoryMarshal.Read<int>(header.AsSpan(1));

                if (payloadLength < 0)
                {
                    throw new InvalidOperationException("Negative payload length encountered on pipe channel.");
                }

                var payload = ArrayPool<byte>.Shared.Rent(payloadLength);
                try
                {
                    if (payloadLength > 0 && !await ReadExactAsync(_inbound, payload.AsMemory(0, payloadLength), _cts.Token))
                    {
                        break;
                    }

                    var messagePayload = payloadLength == 0
                        ? ReadOnlyMemory<byte>.Empty
                        : payload.AsMemory(0, payloadLength).ToArray();

                    _inbox.Enqueue(new SignalMessage(command, messagePayload));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(payload);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Swallow cancellation on dispose.
        }
        catch
        {
            // Reader failures should tear down the channel to avoid spinning.
            Dispose();
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var remaining = buffer.Length;
        var offset = 0;

        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
            {
                return false; // remote closed
            }

            remaining -= read;
            offset += read;
        }

        return true;
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
        _cts.Cancel();

        try
        {
            _reader.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Ignore failures while tearing down.
        }

        _outbound.Dispose();
        _inbound.Dispose();
        _cts.Dispose();
    }
}
