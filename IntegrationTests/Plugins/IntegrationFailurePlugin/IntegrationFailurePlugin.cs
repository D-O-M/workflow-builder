using Backend.Plugins.Harness;

namespace WorkflowBuilder.IntegrationTests.Plugins.IntegrationFailurePlugin;

public sealed class IntegrationFailurePlugin : IPlugin
{
    public required FailureRequest Request { get; init; }

    public FailureSuccess? Success { get; set; }

    public Task Execute()
    {

        if (Request.ShouldFail)
        {
            throw new InvalidOperationException(
                $"Requested failure for {Request.NormalizedName}.");
        }

        Success = new FailureSuccess
        {
            Outcome = $"{Request.NormalizedName}:{Request.Score}"
        };

        return Task.CompletedTask;
    }
}
