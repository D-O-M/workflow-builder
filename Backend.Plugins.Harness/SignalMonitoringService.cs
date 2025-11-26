using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Backend.Plugins.Communication;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backend.Plugins.Harness;

internal sealed class SignalMonitoringService : BackgroundService
{
    private readonly ILogger<SignalMonitoringService> _logger;
    private readonly SignalInbox _inbox;
    private Stream? _outbound;
    private Stream? _inbound;

    public SignalMonitoringService(
        ILogger<SignalMonitoringService> logger,
        SignalInbox inbox)
    {
        _logger = logger;
        _inbox = inbox;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _outbound = Console.OpenStandardOutput();
        _inbound = Console.OpenStandardInput();

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var inbound = _inbound ?? throw new InvalidOperationException("Signal channel not established.");
            _logger.LogInformation("Listening for signals on standard streams");

            await PumpMessagesAsync(inbound, stoppingToken);
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
        _outbound?.Dispose();
        _inbound?.Dispose();
        _outbound = null;
        _inbound = null;

        await base.StopAsync(cancellationToken);
    }

    private async Task PumpMessagesAsync(Stream inbound, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var message = await SignalEnvelopeStream.ReadAsync(inbound, stoppingToken);
            if (message is null)
            {
                break;
            }

            switch (message.Value.Command)
            {
                case SignalCommand.Action:
                    await HandleActionAsync(message.Value.Payload, stoppingToken);
                    break;
                case SignalCommand.Invocation:
                    await HandleInvocationAsync(message.Value.Payload, stoppingToken);
                    break;
                default:
                    _logger.LogDebug("Ignoring unsupported command {Command}", message.Value.Command);
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

    private async ValueTask HandleInvocationAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var envelope = InvocationEnvelopeSerializer.Deserialize(payload.Span);
        var message = new Signal(
            SignalsMessageKind.Invocation,
            envelope.WorkflowTaskInstanceId.ToString(),
            ModelType: string.Empty,
            ContentType: "application/json",
            Payload: InvocationEnvelopeSerializer.Serialize(envelope).ToArray(),
            Invocation: envelope);

        await _inbox.Writer.WriteAsync(message, cancellationToken);
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
