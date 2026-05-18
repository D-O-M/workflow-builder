using System.Text;
using Backend.Plugins.Communication;

namespace Backend.Plugins.Harness;

internal sealed class PluginProcessProtocolWriter
{
    private readonly TextWriter _output;
    private readonly SemaphoreSlim _outputGate;

    public PluginProcessProtocolWriter(TextWriter output, SemaphoreSlim outputGate)
    {
        _output = output;
        _outputGate = outputGate;
    }

    public Task WriteReadyAsync(CancellationToken cancellationToken) =>
        WriteMessageAsync(PluginProcessMessageSerializer.Ready(), cancellationToken);

    public Task WriteResultAsync(
        PluginExecutionResult result,
        CancellationToken cancellationToken) =>
        WriteMessageAsync(PluginProcessMessageSerializer.Result(result), cancellationToken);

    public Task WriteLogAsync(
        string message,
        CancellationToken cancellationToken = default) =>
        string.IsNullOrEmpty(message)
            ? Task.CompletedTask
            : WriteMessageAsync(PluginProcessMessageSerializer.Log(message), cancellationToken);

    public Task WriteErrorAsync(
        string message,
        Exception? exception = null,
        Guid? jobId = null,
        CancellationToken cancellationToken = default) =>
        WriteMessageAsync(PluginProcessMessageSerializer.Error(message, exception, jobId), cancellationToken);

    public Task WriteHeartbeatAsync(CancellationToken cancellationToken) =>
        WriteMessageAsync(PluginProcessMessageSerializer.Heartbeat(), cancellationToken);

    private async Task WriteMessageAsync(
        PluginProcessMessage message,
        CancellationToken cancellationToken)
    {
        var serializedMessage = PluginProcessMessageSerializer.Serialize(message);
        await _outputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _output.WriteLineAsync(serializedMessage).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _outputGate.Release();
        }
    }
}

internal sealed class PluginProcessLogTextWriter : TextWriter
{
    private readonly PluginProcessProtocolWriter _protocolWriter;
    private readonly StringBuilder _buffer = new();
    private readonly object _sync = new();

    public PluginProcessLogTextWriter(PluginProcessProtocolWriter protocolWriter)
    {
        _protocolWriter = protocolWriter;
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (_sync)
        {
            if (value == '\n')
            {
                FlushBufferedLine();
                return;
            }

            if (value != '\r')
            {
                _buffer.Append(value);
            }
        }
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        lock (_sync)
        {
            foreach (var character in value)
            {
                if (character == '\n')
                {
                    FlushBufferedLine();
                }
                else if (character != '\r')
                {
                    _buffer.Append(character);
                }
            }
        }
    }

    public override void WriteLine()
    {
        lock (_sync)
        {
            FlushBufferedLine();
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_sync)
        {
            if (!string.IsNullOrEmpty(value))
            {
                _buffer.Append(value);
            }

            FlushBufferedLine();
        }
    }

    public override Task WriteAsync(char value)
    {
        Write(value);
        return Task.CompletedTask;
    }

    public override Task WriteAsync(string? value)
    {
        Write(value);
        return Task.CompletedTask;
    }

    public override Task WriteLineAsync(string? value)
    {
        WriteLine(value);
        return Task.CompletedTask;
    }

    public override Task WriteLineAsync()
    {
        WriteLine();
        return Task.CompletedTask;
    }

    public override void Flush()
    {
        lock (_sync)
        {
            FlushBufferedLine();
        }
    }

    public override Task FlushAsync()
    {
        Flush();
        return Task.CompletedTask;
    }

    private void FlushBufferedLine()
    {
        if (_buffer.Length == 0)
        {
            return;
        }

        var message = _buffer.ToString();
        _buffer.Clear();
        _protocolWriter.WriteLogAsync(message).GetAwaiter().GetResult();
    }
}
