using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.Plugins.Communication;
using Microsoft.Extensions.Logging;

namespace Backend.Engine;

public sealed class NanoServiceSignalsClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions ModelSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly TimeSpan OutputIdleTimeout = TimeSpan.FromSeconds(1);

    private readonly ILogger<NanoServiceSignalsClient> _logger;
    private SignalConnection? _connection;

    public ISignalChannel? Channel => _connection?.Channel;

    public NanoServiceSignalsClient(ILogger<NanoServiceSignalsClient> logger)
    {
        _logger = logger;
    }

    public async Task EstablishAsync(
        Process process,
        string nanoServiceName,
        IEnumerable<NanoServiceInputChannelModel> inputChannels,
        NanoServiceGlobalStateSnapshot? globalState,
        NamedPipeChannelOptions channelOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentException.ThrowIfNullOrEmpty(nanoServiceName);

        _connection = new SignalConnection(
            channelOptions,
            await NamedPipeSignalChannel.CreateServerAsync(
                channelOptions.OutboundPipeName,
                channelOptions.InboundPipeName,
                cancellationToken));

        await SendInputChannelsAsync(inputChannels, cancellationToken);
        if (globalState is not null)
        {
            await SendGlobalStateAsync(globalState, cancellationToken);
        }
    }

    private async Task SendInputChannelsAsync(
        IEnumerable<NanoServiceInputChannelModel> channels,
        CancellationToken cancellationToken)
    {
        foreach (var channel in channels)
        {
            var action = new SignalAction(
                SignalActionClass.InputChannel,
                channel.ChannelName,
                channel.ModelType,
                ContentType: "application/json",
                Data: SerializeModel(channel.Model));

            await SendAsync(SignalCommand.Action, SignalActionSerializer.Serialize(action), cancellationToken);
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

        await SendAsync(SignalCommand.Action, SignalActionSerializer.Serialize(action), cancellationToken);
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

    public Task SendActionAsync(SignalAction action, CancellationToken cancellationToken)
        => SendAsync(SignalCommand.Action, SignalActionSerializer.Serialize(action), cancellationToken);

    private async Task SendAsync(SignalCommand command, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var channel = _connection?.Channel ?? throw new InvalidOperationException("Nano service channel is not ready yet.");

        var message = new SignalMessage(command, payload);

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

    public async Task<IReadOnlyCollection<SignalAction>> ReceiveOutputsAsync(CancellationToken cancellationToken)
    {
        var channel = _connection?.Channel ?? throw new InvalidOperationException("Nano service channel is not ready yet.");

        var outputs = new List<SignalAction>();
        var idle = Stopwatch.StartNew();

        while (!cancellationToken.IsCancellationRequested && idle.Elapsed < OutputIdleTimeout)
        {
            if (!channel.TryReceive(out var message))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
                continue;
            }

            idle.Restart();

            if (message.Command != SignalCommand.Action)
            {
                _logger.LogDebug("Ignoring non-action command {Command} while awaiting outputs", message.Command);
                continue;
            }

            var action = SignalActionSerializer.Deserialize(message.Payload.Span);
            if (action.Class == SignalActionClass.OutputChannel)
            {
                outputs.Add(action);
            }
        }

        return outputs;
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
