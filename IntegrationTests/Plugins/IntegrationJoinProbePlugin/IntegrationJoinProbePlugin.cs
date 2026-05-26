using Backend.Plugins.Harness;

namespace WorkflowBuilder.IntegrationTests.Plugins.IntegrationJoinProbePlugin;

public sealed class IntegrationJoinProbePlugin : IPlugin
{
    public required JoinProbeRequest Request { get; init; }

    public JoinProbeSuccess? Success { get; set; }

    public Task Execute()
    {

        Success = new JoinProbeSuccess
        {
            Summary = $"{Request.LeftText}|{Request.RightText}|{Request.CombinedScore}|{Request.Mode}",
            CombinedScore = Request.CombinedScore,
            Mode = Request.Mode
        };

        return Task.CompletedTask;
    }
}
