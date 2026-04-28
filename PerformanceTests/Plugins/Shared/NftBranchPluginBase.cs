using Backend.Plugins.Harness;

namespace WorkflowBuilder.PerformanceTests.Plugins;

public abstract class NftBranchPluginBase : IPlugin
{
    public required NftProcessState State { get; set; }

    public required NftBranchRequest Request { get; init; }

    public NftBranchResult Result { get; set; } = default!;

    protected abstract int BranchBias { get; }

    public async Task Execute()
    {
        if (Request.DelayMs is > 0)
        {
            await Task.Delay(Request.DelayMs.Value);
        }

        var branchName = string.IsNullOrWhiteSpace(Request.BranchName)
            ? GetType().Name
            : Request.BranchName.Trim();
        var branchWeight = Math.Max(1, Request.BranchWeight);
        var branchScore = Request.SeedScore + BranchBias * branchWeight;
        var digest = $"{branchName}:{Request.PayloadToken.Length}:{branchScore}";

        State.StepsCompleted += 1;
        State.TotalScore += branchScore;
        State.LastStage = branchName.ToLowerInvariant();

        Result = new NftBranchResult
        {
            CorrelationKey = Request.CorrelationKey,
            BranchName = branchName,
            BranchScore = branchScore,
            BranchDigest = digest
        };
    }
}
