namespace WorkflowBuilder.PerformanceTests.Plugins;

public sealed class NftSeedPlugin : Backend.Plugins.Harness.IPlugin
{
    public required NftSeedRequest Request { get; init; }

    public NftSeedContext Context { get; set; } = default!;

    public async Task Execute()
    {
        if (Request.DelayMs is > 0)
        {
            await Task.Delay(Request.DelayMs.Value);
        }

        var correlationKey = string.IsNullOrWhiteSpace(Request.CorrelationKey)
            ? Guid.NewGuid().ToString("N")
            : Request.CorrelationKey.Trim();
        var payloadSize = Math.Clamp(Request.PayloadSize, 1, 4096);
        var region = string.IsNullOrWhiteSpace(Request.Region)
            ? "local"
            : Request.Region.Trim().ToLowerInvariant();
        var score = Request.BaseScore + payloadSize % 17 + 1;

        Context = new NftSeedContext
        {
            CorrelationKey = correlationKey,
            PayloadToken = BuildPayloadToken(correlationKey, payloadSize),
            SeedScore = score,
            Region = region
        };
    }

    private static string BuildPayloadToken(string correlationKey, int payloadSize)
    {
        var prefix = correlationKey.Length <= 12 ? correlationKey : correlationKey[..12];
        return $"{prefix}:{payloadSize}:{Math.Abs(correlationKey.GetHashCode()) % 10_000}";
    }
}
