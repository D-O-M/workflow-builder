namespace Backend.API.Observability;

public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    public string GrafanaBaseUrl { get; init; } = "http://localhost:3000";
    public string LokiBaseUrl { get; init; } = "http://localhost:3100";
    public string TempoBaseUrl { get; init; } = "http://localhost:3200";
    public string PrometheusBaseUrl { get; init; } = "http://localhost:9090";
}
