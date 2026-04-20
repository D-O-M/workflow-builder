namespace Backend.API.Infrastructure;

public sealed class ClientRuntimeConfigOptions
{
    public const string SectionName = "ClientConfig";

    public string? ApiBaseUrl { get; init; }

    public string? SignalRBaseUrl { get; init; }

    public string? GrafanaBaseUrl { get; init; }
}
