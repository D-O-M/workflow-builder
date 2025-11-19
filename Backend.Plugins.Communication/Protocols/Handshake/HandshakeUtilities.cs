using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Backend.Plugins.Communication.Protocols.Handshake;

internal static class HandshakeUtilities
{
    public static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[totalRead..], cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of stream while reading handshake data.");
            }

            totalRead += read;
        }
    }

    public static byte ComputeXorChecksum(ReadOnlySpan<byte> data)
    {
        byte sum = 0;
        foreach (var b in data)
        {
            sum ^= b;
        }

        return sum;
    }
}
