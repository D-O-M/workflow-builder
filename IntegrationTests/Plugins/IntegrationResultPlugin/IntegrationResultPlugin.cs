using Backend.Plugins.Harness;

namespace WorkflowBuilder.IntegrationTests.Plugins.IntegrationResultPlugin;

public sealed class IntegrationResultPlugin : IPlugin
{
    public required IntegrationWorkflowState State { get; set; }

    public required ResultRequest Request { get; init; }

    public ResultSuccess? Success { get; set; }

    public Task Execute()
    {
        State.StepsCompleted += 1;
        State.LastStage = "result";

        Success = new ResultSuccess
        {
            Summary = $"Completed {Request.Message}",
            FinalScore = Request.FinalScore
        };

        return Task.CompletedTask;
    }
}
