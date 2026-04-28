using Backend.Plugins.Harness;

namespace WorkflowBuilder.PerformanceTests.Plugins;

public sealed class NftJoinPlugin : IPlugin
{
    public required NftProcessState State { get; set; }

    public required NftJoinRequest Request { get; init; }

    public NftJoinResult Joined { get; set; } = default!;

    public Task Execute()
    {
        var combinedScore = Request.AlphaScore + Request.BetaScore + Request.GammaScore;
        var summary = string.Join(
            "|",
            Request.CorrelationKey,
            Request.Mode,
            Request.AlphaDigest,
            Request.BetaDigest,
            Request.GammaDigest,
            combinedScore);

        State.StepsCompleted += 1;
        State.TotalScore += combinedScore;
        State.LastStage = "join";

        Joined = new NftJoinResult
        {
            CorrelationKey = Request.CorrelationKey,
            CombinedScore = combinedScore,
            Summary = summary
        };

        return Task.CompletedTask;
    }
}
