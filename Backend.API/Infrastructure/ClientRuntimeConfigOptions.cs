using Backend.Topology;

namespace Backend.API.Infrastructure;

public sealed class ClientRuntimeConfigOptions
{
    public const string SectionName = WorkflowBuilderTopology.ClientConfig.SectionName;

    public string? ApiBaseUrl { get; init; }

    public string? SignalRBaseUrl { get; init; }

    public string? GrafanaBaseUrl { get; init; }

    public ClientRuntimeAuthenticationOptions Authentication { get; init; } = new();
}

public sealed class ClientRuntimeAuthenticationOptions
{
    public bool? Enabled { get; init; }

    public string? Authority { get; init; }

    public string? ClientId { get; init; }

    public string? Scope { get; init; }

    public string? Audience { get; init; }
}
