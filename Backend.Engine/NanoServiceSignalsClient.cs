using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Backend.Plugins.Communication;

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
    private Stream? _outboundStream;
    private Stream? _inboundStream;

    public NanoServiceSignalsClient(ILogger<NanoServiceSignalsClient> logger)
    {
        _logger = logger;
    }

    public async Task EstablishAsync(
        Process process,
        string nanoServiceName,
        Stream outboundStream,
        Stream inboundStream,
        IEnumerable<NanoServiceInputChannelModel> inputChannels,
        NanoServiceGlobalStateSnapshot? globalState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentException.ThrowIfNullOrEmpty(nanoServiceName);
        ArgumentNullException.ThrowIfNull(outboundStream);
        ArgumentNullException.ThrowIfNull(inboundStream);

        _logger.LogInformation("Establishing standard stream signals for {NanoService}", nanoServiceName);

        _outboundStream = outboundStream;
        _inboundStream = inboundStream;

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

    public Task SendInvocationAsync(InvocationEnvelope invocation, CancellationToken cancellationToken)
        => SendAsync(SignalCommand.Invocation, InvocationEnvelopeSerializer.Serialize(invocation), cancellationToken);

    private async Task SendAsync(SignalCommand command, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var outbound = _outboundStream ?? throw new InvalidOperationException("Nano service channel is not ready yet.");
        await SignalEnvelopeStream.WriteAsync(outbound, new SignalMessage(command, payload), cancellationToken);
    }

    public async Task<IReadOnlyCollection<SignalAction>> ReceiveOutputsAsync(CancellationToken cancellationToken)
    {
        var inbound = _inboundStream ?? throw new InvalidOperationException("Nano service channel is not ready yet.");

        var outputs = new List<SignalAction>();
        var idle = Stopwatch.StartNew();

        while (!cancellationToken.IsCancellationRequested && idle.Elapsed < OutputIdleTimeout)
        {
            var message = await SignalEnvelopeStream.ReadAsync(inbound, cancellationToken);
            if (message is null)
            {
                break;
            }

            idle.Restart();

            if (message.Value.Command != SignalCommand.Action)
            {
                _logger.LogDebug("Ignoring non-action command {Command} while awaiting outputs", message.Value.Command);
                continue;
            }

            var action = SignalActionSerializer.Deserialize(message.Value.Payload.Span);
            if (action.Class == SignalActionClass.OutputChannel)
            {
                outputs.Add(action);
            }
        }

        return outputs;
    }

    public async ValueTask DisposeAsync()
    {
        _outboundStream = null;
        _inboundStream = null;
    }
}
