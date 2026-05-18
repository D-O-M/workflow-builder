namespace Backend.Engine;

public sealed record PluginAgentExitNotification(
    string PluginName,
    int ExitCode,
    bool ShutdownRequested,
    bool ExitedBeforeReady,
    IReadOnlyCollection<Guid> InFlightProcessStepIds);

public interface IPluginAgentFailureHandler
{
    Task HandlePluginAgentExitedAsync(
        PluginAgentExitNotification notification,
        CancellationToken cancellationToken);
}
