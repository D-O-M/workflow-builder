namespace WorkflowBuilder.IntegrationTests.Infrastructure;

internal sealed class PluginBundleCatalog
{
    public const string SeedPlugin = "IntegrationSeedPlugin";
    public const string TransformPlugin = "IntegrationTransformPlugin";
    public const string ResultPlugin = "IntegrationResultPlugin";
    public const string FailurePlugin = "IntegrationFailurePlugin";
    public const string JoinProbePlugin = "IntegrationJoinProbePlugin";

    private readonly IReadOnlyDictionary<string, string> _bundlePathsByPluginName;

    public PluginBundleCatalog(IReadOnlyDictionary<string, string> bundlePathsByPluginName)
    {
        _bundlePathsByPluginName = bundlePathsByPluginName;
    }

    public IEnumerable<string> PluginNames => _bundlePathsByPluginName.Keys;

    public string this[string pluginName] => _bundlePathsByPluginName[pluginName];
}
