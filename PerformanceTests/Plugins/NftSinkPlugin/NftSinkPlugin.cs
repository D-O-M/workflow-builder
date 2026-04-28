using Backend.Plugins.Harness;

namespace WorkflowBuilder.PerformanceTests.Plugins;

public sealed class NftSinkPlugin : IPlugin
{
    public required NftProcessState State { get; set; }

    public required NftSinkRequest Request { get; init; }

    public NftCompletion Completion { get; set; } = default!;

    public Task Execute()
    {
        var accepted = Request.CombinedScore >= Request.ExpectedMinimumScore;

        State.StepsCompleted += 1;
        State.LastStage = "sink";

        Completion = new NftCompletion
        {
            CorrelationKey = Request.CorrelationKey,
            Accepted = accepted,
            ResultCode = accepted ? "accepted" : "below-threshold"
        };

        return accepted
            ? Task.CompletedTask
            : throw new InvalidDataException(
                $"CombinedScore {Request.CombinedScore} is below expected minimum {Request.ExpectedMinimumScore}.");
    }
}
