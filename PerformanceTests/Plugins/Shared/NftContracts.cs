using Backend.Plugins.Harness;

namespace WorkflowBuilder.PerformanceTests.Plugins;

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class NftSeedRequest
{
    public string CorrelationKey { get; set; } = string.Empty;

    public int PayloadSize { get; set; }

    public int BaseScore { get; set; }

    public string Region { get; set; } = string.Empty;

    public int? DelayMs { get; set; }
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class NftSeedContext
{
    public string CorrelationKey { get; set; } = string.Empty;

    public string PayloadToken { get; set; } = string.Empty;

    public int SeedScore { get; set; }

    public string Region { get; set; } = string.Empty;
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class NftBranchRequest
{
    public string CorrelationKey { get; set; } = string.Empty;

    public string PayloadToken { get; set; } = string.Empty;

    public int SeedScore { get; set; }

    public string BranchName { get; set; } = string.Empty;

    public int BranchWeight { get; set; }

    public int? DelayMs { get; set; }
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class NftBranchResult
{
    public string CorrelationKey { get; set; } = string.Empty;

    public string BranchName { get; set; } = string.Empty;

    public int BranchScore { get; set; }

    public string BranchDigest { get; set; } = string.Empty;
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class NftJoinRequest
{
    public string CorrelationKey { get; set; } = string.Empty;

    public int AlphaScore { get; set; }

    public int BetaScore { get; set; }

    public int GammaScore { get; set; }

    public string AlphaDigest { get; set; } = string.Empty;

    public string BetaDigest { get; set; } = string.Empty;

    public string GammaDigest { get; set; } = string.Empty;

    public string Mode { get; set; } = string.Empty;
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class NftJoinResult
{
    public string CorrelationKey { get; set; } = string.Empty;

    public int CombinedScore { get; set; }

    public string Summary { get; set; } = string.Empty;
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class NftSinkRequest
{
    public string CorrelationKey { get; set; } = string.Empty;

    public int CombinedScore { get; set; }

    public string Summary { get; set; } = string.Empty;

    public int ExpectedMinimumScore { get; set; }
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class NftCompletion
{
    public string CorrelationKey { get; set; } = string.Empty;

    public bool Accepted { get; set; }

    public string ResultCode { get; set; } = string.Empty;
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class NftLaneRequest
{
    public string CorrelationKey { get; set; } = string.Empty;

    public string PayloadToken { get; set; } = string.Empty;

    public int SeedScore { get; set; }

    public string LaneName { get; set; } = string.Empty;

    public int LaneWeight { get; set; }

    public int? DelayMs { get; set; }
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class NftLaneResult
{
    public string CorrelationKey { get; set; } = string.Empty;

    public string LaneName { get; set; } = string.Empty;

    public int LaneScore { get; set; }

    public string LaneDigest { get; set; } = string.Empty;
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class NftCollectionSeedRequest
{
    public string CorrelationKey { get; set; } = string.Empty;

    public int ItemCount { get; set; }

    public int? DelayMs { get; set; }
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class NftCollectionSeedResult
{
    public string CorrelationKey { get; set; } = string.Empty;

    public string[] Items { get; set; } = [];
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class NftItemSinkRequest
{
    public string Item { get; set; } = string.Empty;

    public int? DelayMs { get; set; }
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class NftItemSinkResult
{
    public string Item { get; set; } = string.Empty;

    public bool Accepted { get; set; }
}
