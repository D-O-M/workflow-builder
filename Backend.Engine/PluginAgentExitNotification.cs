namespace Backend.Engine;

public sealed record PluginAgentExitNotification(
    PluginAgentIdentity Identity,
    int ExitCode,
    bool ShutdownRequested,
    bool ExitedBeforeReady,
    bool PolicyRequestedStop,
    IReadOnlyCollection<Guid> InFlightProcessStepIds)
{
    public string PluginName => Identity.DisplayName;
}
