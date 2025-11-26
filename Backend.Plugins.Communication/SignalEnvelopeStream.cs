using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Backend.Plugins.Communication;

/// <summary>
/// Helper for writing and reading framed signal envelopes over a stream.
/// </summary>
public static class SignalEnvelopeStream
{
    private const int HeaderLength = sizeof(byte) + sizeof(int);

    public static async Task WriteAsync(Stream stream, SignalMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var payloadLength = message.PayloadLength;
        var totalLength = HeaderLength + payloadLength;
        var buffer = ArrayPool<byte>.Shared.Rent(totalLength);

        try
        {
            buffer[0] = (byte)message.Command;
            MemoryMarshal.Write(buffer.AsSpan(1, sizeof(int)), in payloadLength);
            message.Payload.Span.CopyTo(buffer.AsSpan(HeaderLength));

            await stream.WriteAsync(buffer.AsMemory(0, totalLength), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async Task<SignalMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = ArrayPool<byte>.Shared.Rent(HeaderLength);
        try
        {
            if (!await ReadExactAsync(stream, header.AsMemory(0, HeaderLength), cancellationToken))
            {
                return null;
            }

            var command = (SignalCommand)header[0];
            var payloadLength = MemoryMarshal.Read<int>(header.AsSpan(1));
            if (payloadLength < 0)
            {
                throw new InvalidOperationException("Negative payload length encountered in signal envelope.");
            }

            if (payloadLength == 0)
            {
                return new SignalMessage(command, ReadOnlyMemory<byte>.Empty);
            }

            var payload = new byte[payloadLength];
            if (!await ReadExactAsync(stream, payload.AsMemory(0, payloadLength), cancellationToken))
            {
                return null;
            }

            return new SignalMessage(command, payload);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
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
                return false;
            }

            remaining -= read;
            offset += read;
        }

        return true;
    }
}
