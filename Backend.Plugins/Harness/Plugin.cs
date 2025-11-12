namespace Plugins.Harness;

public class Plugin
{
    public static PluginBuilder<TPlugin> CreateBuilder<TPlugin>(string displayName)
        where TPlugin : class, IPlugin
    {
        return new PluginBuilder<TPlugin>(displayName);
    }
}

public class Plugin<TPlugin> : Plugin
    where TPlugin : class, IPlugin
{
    private readonly PluginBuilder<TPlugin> _pluginBuilder;

    public Plugin(PluginBuilder<TPlugin> pluginBuilder)
    {
        _pluginBuilder = pluginBuilder;
    }

    public new static PluginBuilder<TPlugin> CreateBuilder<TPlugin2>()
        where TPlugin2 : class, IPlugin
    {
        throw new InvalidOperationException("The plugin has already been built.");
    }

    public async Task RunAsync() =>
        await _pluginBuilder.RunAsync();
}
