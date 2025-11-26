namespace Backend.Plugins.Communication;

public enum PipeConnectionRole
{
    EngineServer,
    PluginServer
}

/// <summary>
/// Provides the pipe names and correlation id required to establish a named
/// pipe-based signal channel between the engine and plugin harness.
/// </summary>
public sealed class NamedPipeChannelOptions
{
    public Guid SessionId { get; init; }

    public string OutboundPipeName { get; init; } = string.Empty;

    public string InboundPipeName { get; init; } = string.Empty;

    public PipeConnectionRole Role { get; init; } = PipeConnectionRole.EngineServer;

    public NamedPipeChannelOptions()
    {
    }

    public NamedPipeChannelOptions(
        Guid sessionId,
        string outboundPipeName,
        string inboundPipeName,
        PipeConnectionRole role = PipeConnectionRole.EngineServer)
    {
        SessionId = sessionId;
        OutboundPipeName = outboundPipeName;
        InboundPipeName = inboundPipeName;
        Role = role;
    }
}
