using Backend.Plugins.Harness;

namespace WorkflowBuilder.IntegrationTests.Plugins.IntegrationResultPlugin;

public sealed class IntegrationResultPlugin : IPlugin
{
    public required ResultRequest Request { get; init; }

    public ResultSuccess? Success { get; set; }

    public Task Execute()
    {

        Success = new ResultSuccess
        {
            Summary = $"Completed {Request.Message}",
            FinalScore = Request.FinalScore
        };

        return Task.CompletedTask;
    }
}
