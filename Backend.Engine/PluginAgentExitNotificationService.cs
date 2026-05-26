namespace Backend.Engine;

public sealed class PluginAgentExitNotificationService
{
    private readonly PluginAgentHealthEventBus _healthEventBus;
    private readonly PluginRuntimeAgentTelemetry _telemetry;
    private readonly ILogger<PluginAgentExitNotificationService> _logger;

    public PluginAgentExitNotificationService(
        PluginAgentHealthEventBus healthEventBus,
        PluginRuntimeAgentTelemetry telemetry,
        ILogger<PluginAgentExitNotificationService> logger)
    {
        _healthEventBus = healthEventBus;
        _telemetry = telemetry;
        _logger = logger;
    }

    public async Task NotifyProcessExitAsync(
        PluginAgentIdentity identity,
        int exitCode,
        bool shutdownRequested,
        bool policyRequestedStop,
        PluginRuntimeProcessExitSummary exitSummary,
        CancellationToken cancellationToken)
    {
        if (exitSummary.ExitedBeforeReady)
        {
            if (shutdownRequested)
            {
                _logger.LogInformation(
                    "Plugin runtime {PluginName} exited before ready during shutdown. Exit code: {ExitCode}.",
                    identity.DisplayName,
                    exitCode);
            }
            else
            {
                _telemetry.RecordAgentExitedBeforeReady(identity.DisplayName);
                await PublishHealthEventAsync(
                    PluginAgentHealthEvent.Create(
                        identity,
                        PluginAgentHealthEventType.AgentExitedBeforeReady,
                        exitCode: exitCode,
                        message: "Plugin runtime exited before signalling readiness.",
                        policyRequestedStop: policyRequestedStop),
                    cancellationToken);
                _logger.LogError(
                    "Plugin runtime {PluginName} exited before signalling readiness. Exit code: {ExitCode}.",
                    identity.DisplayName,
                    exitCode);
            }
        }

        if (shutdownRequested)
        {
            return;
        }

        if (exitSummary.InFlightJobIds.Count > 0)
        {
            await PublishHealthEventAsync(
                PluginAgentHealthEvent.Create(
                    identity,
                    PluginAgentHealthEventType.AgentExitedWithInFlightJobs,
                    exitSummary.InFlightJobIds,
                    exitCode,
                    message: "Plugin runtime exited with in-flight process steps.",
                    policyRequestedStop: policyRequestedStop),
                cancellationToken);
            return;
        }

        if (!exitSummary.ExitedBeforeReady)
        {
            await PublishHealthEventAsync(
                PluginAgentHealthEvent.Create(
                    identity,
                    PluginAgentHealthEventType.RuntimeLoadFailed,
                    exitCode: exitCode,
                    message: "Plugin runtime exited unexpectedly.",
                    policyRequestedStop: policyRequestedStop),
                cancellationToken);
        }
    }

    private async Task PublishHealthEventAsync(
        PluginAgentHealthEvent healthEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            await _healthEventBus.PublishAsync(healthEvent, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish plugin-agent health event {EventType} for {PluginName}.",
                healthEvent.EventType,
                healthEvent.Identity.DisplayName);
        }
    }
}
