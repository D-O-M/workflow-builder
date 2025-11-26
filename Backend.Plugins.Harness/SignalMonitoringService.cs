using System;
using System.Threading;
using System.Threading.Tasks;
using Backend.Plugins.Communication;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backend.Plugins.Harness;

internal sealed class SignalMonitoringService : BackgroundService
{
    private readonly ILogger<SignalMonitoringService> _logger;
    private readonly SignalInbox _inbox;
    private readonly NamedPipeChannelOptions _channelOptions;
    private SignalConnection? _connection;

    public SignalMonitoringService(
        ILogger<SignalMonitoringService> logger,
        SignalInbox inbox,
        IOptions<NamedPipeChannelOptions> options)
    {
        _logger = logger;
        _inbox = inbox;
        _channelOptions = options.Value;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_channelOptions.OutboundPipeName) ||
            string.IsNullOrWhiteSpace(_channelOptions.InboundPipeName))
        {
            throw new InvalidOperationException("Pipe names were not provided. Ensure Signals:OutboundPipe and Signals:InboundPipe are configured.");
        }

        var channel = await NamedPipeSignalChannel.CreateClientAsync(
            _channelOptions.OutboundPipeName,
            _channelOptions.InboundPipeName,
            cancellationToken);

        _connection = new SignalConnection(_channelOptions, channel);

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var connection = _connection ?? throw new InvalidOperationException("Signal channel not established.");
            _logger.LogInformation("Established session {SessionId}", connection.ChannelOptions.SessionId);

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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task PumpMessagesAsync(ISignalChannel channel, CancellationToken stoppingToken)
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
                case SignalCommand.Action:
                    await HandleActionAsync(message.Payload, stoppingToken);
                    break;
                default:
                    _logger.LogDebug("Ignoring unsupported command {Command}", message.Command);
                    break;
            }
        }
    }

    private async ValueTask HandleActionAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var action = SignalActionSerializer.Deserialize(payload.Span);
        switch (action.Class)
        {
            case SignalActionClass.InputChannel:
                await PublishMessageAsync(
                    SignalsMessageKind.InputChannelModel,
                    action,
                    cancellationToken);
                break;
            case SignalActionClass.OutputChannel:
                await PublishMessageAsync(
                    SignalsMessageKind.OutputChannelModel,
                    action,
                    cancellationToken);
                break;
            case SignalActionClass.GlobalState:
                await PublishMessageAsync(
                    SignalsMessageKind.GlobalState,
                    action,
                    cancellationToken);
                break;
            default:
                _logger.LogDebug("Ignoring unsupported action class {Class}", action.Class);
                break;
        }
    }

    private async ValueTask PublishMessageAsync(
        SignalsMessageKind kind,
        SignalAction action,
        CancellationToken cancellationToken)
    {
        var message = new Signal(
            kind,
            action.Identifier,
            action.Type ?? string.Empty,
            action.ContentType ?? "application/octet-stream",
            action.Data);

        await _inbox.Writer.WriteAsync(message, cancellationToken);
    }
}
