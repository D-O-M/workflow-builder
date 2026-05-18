using Backend.Plugins.Harness;

namespace WorkflowBuilder.PerformanceTests.Plugins;

public sealed class NftCollectionSeedPlugin : IPlugin
{
    public required NftProcessState State { get; set; }

    public required NftCollectionSeedRequest Request { get; init; }

    public NftCollectionSeedResult Collection { get; set; } = default!;

    public async Task Execute()
    {
        if (Request.DelayMs is > 0)
        {
            await Task.Delay(Request.DelayMs.Value);
        }

        var correlationKey = string.IsNullOrWhiteSpace(Request.CorrelationKey)
            ? Guid.NewGuid().ToString("N")
            : Request.CorrelationKey.Trim();
        var itemCount = Math.Clamp(Request.ItemCount, 0, 10_000);
        var items = Enumerable.Range(0, itemCount)
            .Select(index => $"{correlationKey}:{index:D6}")
            .ToArray();

        State.StepsCompleted += 1;
        State.TotalScore += itemCount;
        State.LastStage = "collection-seed";

        Collection = new NftCollectionSeedResult
        {
            CorrelationKey = correlationKey,
            Items = items
        };
    }
}
