using Backend.Plugins.Harness;

namespace WorkflowBuilder.PerformanceTests.Plugins;

public sealed class NftItemSinkPlugin : IPlugin
{
    public required NftProcessState State { get; set; }

    public required NftItemSinkRequest Request { get; init; }

    public NftItemSinkResult Result { get; set; } = default!;

    public async Task Execute()
    {
        if (Request.DelayMs is > 0)
        {
            await Task.Delay(Request.DelayMs.Value);
        }

        var item = Request.Item.Trim();
        State.StepsCompleted += 1;
        State.TotalScore += item.Length;
        State.LastStage = "item-sink";

        Result = new NftItemSinkResult
        {
            Item = item,
            Accepted = item.Length > 0
        };
    }
}
