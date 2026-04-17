using Backend.Plugins.Harness;

namespace WorkflowBuilder.IntegrationTests.Plugins.IntegrationSeedPlugin;

public sealed class IntegrationSeedPlugin : IPlugin
{
    public required IntegrationWorkflowState State { get; set; }

    public required SeedRequest Request { get; init; }

    public SeedSuccess? Success { get; set; }

    public Task Execute()
    {
        var normalizedName = Request.Name.Trim().ToUpperInvariant();
        var nextScore = Request.BaseScore + 1;

        State.StepsCompleted += 1;
        State.TotalScore += nextScore;
        State.LastStage = "seed";

        Success = new SeedSuccess
        {
            NormalizedName = normalizedName,
            Score = nextScore
        };

        return Task.CompletedTask;
    }
}
