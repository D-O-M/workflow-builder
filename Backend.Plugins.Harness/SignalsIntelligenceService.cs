using Backend.Plugins.Communication;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backend.Plugins.Harness;

internal sealed class SignalsIntelligenceService : BackgroundService
{
    private readonly ILogger<SignalsIntelligenceService> _logger;
    private readonly SignalsIntelligenceInbox _inbox;
    private readonly SignalClient _signalClient;

    public SignalsIntelligenceService(
        ILogger<SignalsIntelligenceService> logger,
        SignalsIntelligenceInbox inbox,
        SignalClient signalClient)
    {
        _logger = logger;
        _inbox = inbox;
        _signalClient = signalClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var connection = await _signalClient.EstablishAsync(stoppingToken);
            _logger.LogInformation("Established session {SessionId}" , connection.Handshake.SessionId);

            await PumpMessagesAsync(connection.Channel, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Signals intelligence channel terminated unexpectedly.");
            throw;
        }
    }

    private async Task PumpMessagesAsync(INanoServiceChannel channel, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!channel.TryReceive(out var message))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), stoppingToken);
                continue;
            }

            switch (message.Command)
            {
                case NanoServiceCommand.ModelWrite:
                    await HandleChannelModelAsync(message.Payload, stoppingToken);
                    break;
                case NanoServiceCommand.StatePush:
                    await HandleGlobalStateAsync(message.Payload, stoppingToken);
                    break;
                default:
                    _logger.LogDebug("Ignoring unsupported command {Command}", message.Command);
                    break;
            }
        }
    }

    private async ValueTask HandleChannelModelAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var envelope = EnginePayloadSerializer.DeserializeChannelModel(payload.Span);
        var message = new SignalsIntelligenceMessage(
            SignalsIntelligenceMessageKind.InputChannelModel,
            envelope.ChannelName,
            envelope.ModelType,
            envelope.ContentType,
            envelope.ModelData);

        await _inbox.Writer.WriteAsync(message, cancellationToken);
    }

    private async ValueTask HandleGlobalStateAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var envelope = EnginePayloadSerializer.DeserializeGlobalState(payload.Span);
        var message = new SignalsIntelligenceMessage(
            SignalsIntelligenceMessageKind.GlobalState,
            identifier: envelope.StateType,
            ModelType: envelope.StateType,
            ContentType: envelope.ContentType,
            Payload: envelope.StateData);

        await _inbox.Writer.WriteAsync(message, cancellationToken);
    }
}
