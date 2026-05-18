using System.Threading.Channels;

namespace Backend.Engine;

public sealed class PluginRuntimeOptions
{
    public int MaxConcurrentResultFinalizations { get; init; } = Environment.ProcessorCount * 4;

    public int ResultFinalizationQueueCapacity { get; init; } = 1024;

    public BoundedChannelFullMode ResultFinalizationQueueFullMode { get; init; } = BoundedChannelFullMode.Wait;

    public int EffectiveMaxConcurrentResultFinalizations =>
        Math.Clamp(MaxConcurrentResultFinalizations, 1, 512);

    public int EffectiveResultFinalizationQueueCapacity =>
        Math.Clamp(ResultFinalizationQueueCapacity, 1, 1_000_000);

    public void Validate()
    {
        if (ResultFinalizationQueueCapacity < 1)
        {
            throw new InvalidOperationException(
                "PluginRuntime:ResultFinalizationQueueCapacity must be at least 1.");
        }

        if (ResultFinalizationQueueFullMode is not BoundedChannelFullMode.Wait)
        {
            throw new InvalidOperationException(
                "PluginRuntime:ResultFinalizationQueueFullMode must be Wait. Drop modes are not supported because plugin result finalization is lossless.");
        }
    }
}
