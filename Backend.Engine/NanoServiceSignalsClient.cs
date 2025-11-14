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
    private INanoServiceChannel? _channel;

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

        var handshake = NanoServiceProtocolFactory.CreateHandshake(nanoServiceName, Environment.ProcessId);
        await PerformHandshakeAsync(process, handshake, cancellationToken);

        _channel = NanoServiceProtocolFactory.CreateEngineChannel(handshake);

        await SendInputChannelsAsync(inputChannels, cancellationToken);
        if (globalState is not null)
        {
            await SendGlobalStateAsync(globalState, cancellationToken);
        }
    }

    private static async Task PerformHandshakeAsync(Process process, NanoServiceHandshake handshake, CancellationToken cancellationToken)
    {
        var stdin = process.StandardInput;
        var stdout = process.StandardOutput;

        if (stdin is null || stdout is null)
        {
            throw new InvalidOperationException("Nano service process was started without redirected STDIO streams.");
        }

        var payload = NanoServiceHandshakeSerializer.Serialize(handshake);
        await stdin.WriteLineAsync(payload).WaitAsync(cancellationToken);
        await stdin.FlushAsync().WaitAsync(cancellationToken);

        var expectedAck = $"{NanoServiceProtocolConstants.HandshakeAcknowledgementPrefix}:{handshake.SessionId:N}";
        var ack = await stdout.ReadLineAsync().WaitAsync(cancellationToken);
        if (!string.Equals(ack, expectedAck, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unexpected nano service handshake acknowledgement. Expected '{expectedAck}' but received '{ack ?? "<null>"}'.");
        }
    }

    private async Task SendInputChannelsAsync(IEnumerable<NanoServiceInputChannelModel> channels, CancellationToken cancellationToken)
    {
        foreach (var channel in channels)
        {
            var payload = new EngineChannelModelPayload(
                channel.ChannelName,
                channel.ModelType,
                contentType: "application/json",
                ModelData: SerializeModel(channel.Model));

            await SendAsync(new NanoServiceMessage(NanoServiceCommand.ModelWrite, EnginePayloadSerializer.SerializeChannelModel(payload)), cancellationToken);
            _logger.LogInformation("Sent model for channel {ChannelName}", channel.ChannelName);
        }
    }

    private async Task SendGlobalStateAsync(NanoServiceGlobalStateSnapshot snapshot, CancellationToken cancellationToken)
    {
        var payload = new EngineGlobalStatePayload(
            snapshot.StateType,
            contentType: "application/json",
            StateData: SerializeModel(snapshot.State));

        await SendAsync(new NanoServiceMessage(NanoServiceCommand.StatePush, EnginePayloadSerializer.SerializeGlobalState(payload)), cancellationToken);
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

    private async Task SendAsync(NanoServiceMessage message, CancellationToken cancellationToken)
    {
        if (_channel is null)
        {
            throw new InvalidOperationException("Nano service channel is not ready yet.");
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            if (_channel.TrySend(message))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            _channel.Dispose();
            _channel = null;
        }
        await Task.CompletedTask;
    }
}
