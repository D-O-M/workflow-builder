using Backend.Plugins.Harness;

namespace WorkflowBuilder.IntegrationTests.Plugins.IntegrationSeedPlugin;

public sealed class IntegrationSeedPlugin : IPlugin
{
    public required SeedRequest Request { get; init; }

    public SeedSuccess? Success { get; set; }

    public Task Execute()
    {
        var normalizedName = Request.Name.Trim().ToUpperInvariant();
        var nextScore = Request.BaseScore + 1;

        Success = new SeedSuccess
        {
            NormalizedName = normalizedName,
            Score = nextScore
        };

        return Task.CompletedTask;
    }
}
