namespace WorkflowBuilder.IntegrationTests.Infrastructure;

internal sealed class IntegrationTestSettings
{
    private IntegrationTestSettings() { }

    public string RunVersion { get; init; } = string.Empty;

    public TimeSpan ResourceTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    public static IntegrationTestSettings Create()
    {
        var now = DateTimeOffset.UtcNow;

        return new IntegrationTestSettings
        {
            RunVersion = $"1.{now:yyMMdd}.{now:HHmm}.{now.Second}"
        };
    }
}
