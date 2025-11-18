using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using Backend.Plugins.Communication;
using Microsoft.Extensions.Logging;

namespace Backend.Engine;

/// <summary>
/// Encapsulates the STDIO handshake and memory-mapped protocol used by each
/// nano service instance.
/// </summary>
public sealed class NanoServiceSignalsClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions ModelSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly ILogger<NanoServiceSignalsClient> _logger;
    private SignalConnection? _connection;

    public NanoServiceSignalsClient(ILogger<NanoServiceSignalsClient> logger)
    {
        _logger = logger;
    }

    public async Task EstablishAsync(
        Process process,
        string nanoServiceName,
        IEnumerable<NanoServiceInputChannelModel> inputChannels,
        NanoServiceGlobalStateSnapshot? globalState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentException.ThrowIfNullOrEmpty(nanoServiceName);

        var stdin = process.StandardInput;
        var stdout = process.StandardOutput;

        if (stdin is null || stdout is null)
        {
            throw new InvalidOperationException("Nano service process was started without redirected STDIO streams.");
        }

        var handshake = SignalProtocolFactory.CreateHandshake(nanoServiceName, Environment.ProcessId);
        var broadcaster = new SignalBroadcaster(stdin, stdout);
        _connection = await broadcaster.EstablishAsync(handshake, cancellationToken);

        await SendInputChannelsAsync(inputChannels, cancellationToken);
        if (globalState is not null)
        {
            await SendGlobalStateAsync(globalState, cancellationToken);
        }
    }

    private async Task SendInputChannelsAsync(IEnumerable<NanoServiceInputChannelModel> channels, CancellationToken cancellationToken)
    {
        foreach (var channel in channels)
        {
            var action = new SignalAction(
                SignalActionClass.InputChannel,
                channel.ChannelName,
                channel.ModelType,
                ContentType: "application/json",
                Data: SerializeModel(channel.Model));

            await SendAsync(new SignalMessage(SignalCommand.Action, SignalActionSerializer.Serialize(action)), cancellationToken);
            _logger.LogInformation("Sent model for channel {ChannelName}", channel.ChannelName);
        }
    }

    private async Task SendGlobalStateAsync(NanoServiceGlobalStateSnapshot snapshot, CancellationToken cancellationToken)
    {
        var action = new SignalAction(
            SignalActionClass.GlobalState,
            snapshot.StateType,
            Type: snapshot.StateType,
            ContentType: "application/json",
            Data: SerializeModel(snapshot.State));

        await SendAsync(new SignalMessage(SignalCommand.Action, SignalActionSerializer.Serialize(action)), cancellationToken);
        _logger.LogInformation("Pushed global state {StateType}", snapshot.StateType);
    }

    private static byte[] SerializeModel(object model)
    {
        if (model is byte[] raw)
        {
            return raw;
        }

        return JsonSerializer.SerializeToUtf8Bytes(model, model.GetType(), ModelSerializerOptions);
    }

    private async Task SendAsync(SignalMessage message, CancellationToken cancellationToken)
    {
        var channel = _connection?.Channel;
        if (channel is null)
        {
            throw new InvalidOperationException("Nano service channel is not ready yet.");
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            if (channel.TrySend(message))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }
}
