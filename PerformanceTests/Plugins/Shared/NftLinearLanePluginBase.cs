using Backend.Plugins.Harness;

namespace WorkflowBuilder.PerformanceTests.Plugins;

public abstract class NftLinearLanePluginBase : IPlugin
{
    public required NftProcessState State { get; set; }

    public required NftLaneRequest Request { get; init; }

    public NftLaneResult Result { get; set; } = default!;

    public async Task Execute()
    {
        if (Request.DelayMs is > 0)
        {
            await Task.Delay(Request.DelayMs.Value);
        }

        var laneName = string.IsNullOrWhiteSpace(Request.LaneName)
            ? "lane"
            : Request.LaneName.Trim();
        var laneWeight = Math.Max(1, Request.LaneWeight);
        var laneScore = Request.SeedScore + laneWeight;
        var digest = $"{laneName}:{Request.PayloadToken.Length}:{laneScore}";

        State.StepsCompleted += 1;
        State.TotalScore += laneScore;
        State.LastStage = laneName.ToLowerInvariant();

        Result = new NftLaneResult
        {
            CorrelationKey = Request.CorrelationKey,
            LaneName = laneName,
            LaneScore = laneScore,
            LaneDigest = digest
        };
    }
}
