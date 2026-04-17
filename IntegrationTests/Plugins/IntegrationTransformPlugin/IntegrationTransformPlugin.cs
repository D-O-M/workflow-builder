using Backend.Plugins.Harness;

namespace WorkflowBuilder.IntegrationTests.Plugins.IntegrationTransformPlugin;

public sealed class IntegrationTransformPlugin : IPlugin
{
    public required IntegrationWorkflowState State { get; set; }

    public required TransformRequest Request { get; init; }

    public TransformSuccess? Success { get; set; }

    public Task Execute()
    {
        var amplifiedScore = Request.Score * 3;

        State.StepsCompleted += 1;
        State.TotalScore += amplifiedScore;
        State.LastStage = "transform";

        Success = new TransformSuccess
        {
            Message = $"{Request.NormalizedName}:{amplifiedScore}",
            FinalScore = amplifiedScore
        };

        return Task.CompletedTask;
    }
}
