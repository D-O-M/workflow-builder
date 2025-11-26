namespace Backend.Plugins.Communication;

/// <summary>
/// Provides the pipe names and correlation id required to establish a named
/// pipe-based signal channel between the engine and plugin harness.
/// </summary>
public sealed class NamedPipeChannelOptions
{
    public Guid SessionId { get; init; }

    public string OutboundPipeName { get; init; } = string.Empty;

    public string InboundPipeName { get; init; } = string.Empty;

    public NamedPipeChannelOptions()
    {
    }

    public NamedPipeChannelOptions(Guid sessionId, string outboundPipeName, string inboundPipeName)
    {
        SessionId = sessionId;
        OutboundPipeName = outboundPipeName;
        InboundPipeName = inboundPipeName;
    }
}
