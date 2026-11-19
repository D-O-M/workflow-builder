namespace WorkflowBuilder.IntegrationTests.Infrastructure;

internal sealed class IntegrationTestSettings
{
    private IntegrationTestSettings() { }

    public string RunVersion { get; init; } = string.Empty;
    public int EngineReplicas { get; init; } = 1;

    public TimeSpan AppHostStartupTimeout { get; init; } = TimeSpan.FromMinutes(6);

    public TimeSpan AppHostShutdownTimeout { get; init; } = TimeSpan.FromSeconds(45);

    public TimeSpan DiagnosticsTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan ResourceTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    public static IntegrationTestSettings Create()
    {
        var now = DateTimeOffset.UtcNow;

        return new IntegrationTestSettings
        {
            RunVersion = $"1.{now:yyMMdd}.{now:HHmm}.{now.Second}",
            EngineReplicas = int.Parse(Environment.GetEnvironmentVariable("WORKFLOWBUILDER_TEST_ENGINE_REPLICAS") ?? "1")
        };
    }
}
