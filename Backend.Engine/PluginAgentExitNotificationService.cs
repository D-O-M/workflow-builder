namespace Backend.Engine;

public sealed class PluginAgentExitNotificationService
{
    private readonly IPluginAgentFailureHandler _failureHandler;
    private readonly PluginRuntimeAgentTelemetry _telemetry;
    private readonly ILogger<PluginAgentExitNotificationService> _logger;

    public PluginAgentExitNotificationService(
        IPluginAgentFailureHandler failureHandler,
        PluginRuntimeAgentTelemetry telemetry,
        ILogger<PluginAgentExitNotificationService> logger)
    {
        _failureHandler = failureHandler;
        _telemetry = telemetry;
        _logger = logger;
    }

    public async Task NotifyProcessExitAsync(
        string pluginName,
        int exitCode,
        bool shutdownRequested,
        PluginRuntimeProcessExitSummary exitSummary,
        CancellationToken cancellationToken)
    {
        if (exitSummary.ExitedBeforeReady)
        {
            if (shutdownRequested)
            {
                _logger.LogInformation(
                    "Plugin runtime {PluginName} exited before ready during shutdown. Exit code: {ExitCode}.",
                    pluginName,
                    exitCode);
            }
            else
            {
                _telemetry.RecordAgentExitedBeforeReady(pluginName);
                _logger.LogError(
                    "Plugin runtime {PluginName} exited before signalling readiness. Exit code: {ExitCode}.",
                    pluginName,
                    exitCode);
            }
        }

        if (shutdownRequested || exitSummary.InFlightJobIds.Count == 0)
        {
            return;
        }

        await _failureHandler.HandlePluginAgentExitedAsync(
            new PluginAgentExitNotification(
                pluginName,
                exitCode,
                ShutdownRequested: false,
                exitSummary.ExitedBeforeReady,
                exitSummary.InFlightJobIds),
            cancellationToken);
    }
}
