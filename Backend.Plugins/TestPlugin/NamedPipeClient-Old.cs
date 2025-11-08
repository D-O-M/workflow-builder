using System.IO;
using System.IO.Pipes;

namespace TestPlugin.Old;

internal class NamedPipeReaderClient : NamedPipeClient
{
    private readonly Lazy<MemoryStream> _stream;

    public NamedPipeReaderClient(string pipeName) : base(pipeName, PipeDirection.In)
    {
        _stream = new Lazy<MemoryStream>(() => new MemoryStream(_pipeClient.InBufferSize));
    }

    public async Task<Memory<byte>> ReadAsync()
    {
        await _pipeClient.ConnectAsync();

        // Read the first 4 bytes to get the length
        byte[] lengthBuffer = new byte[4];
        await _pipeClient.ReadExactlyAsync(lengthBuffer, 0, 4);
        int length = BitConverter.ToInt32(lengthBuffer, 0);

        // Allocate exactly-sized buffer
        var buffer = new byte[length];
        new Span<byte>(buffer);
        var mem = new Memory<byte>(buffer, 0, length);
        await _pipeClient.ReadExactlyAsync(buffer, 0, length);


        _stream.Value.Position = 0;
        _stream.Value.SetLength(0);

        var buffer2 = new byte[_pipeClient.InBufferSize];
        int bytesRead;

        while ((bytesRead = await _pipeClient.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            _stream.Value.Write(buffer, 0, bytesRead);
        }

        return buffer;

        //return await _reader.Value.ReadToEndAsync();
    }
}

internal abstract class NamedPipeClient
{
    protected readonly NamedPipeClientStream _pipeClient;

    public NamedPipeClient(string pipeName, PipeDirection direction)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("Pipe name cannot be null or empty.", nameof(pipeName));
        }

        _pipeClient = new NamedPipeClientStream(".", pipeName, direction);
    }
}
