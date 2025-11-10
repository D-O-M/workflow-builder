namespace Plugins.Harness;

public static class Plugin
{
    public static PluginBuilder<TPlugin> CreateBuilder<TPlugin>()
        where TPlugin : class, IPlugin
    {
        return new PluginBuilder<TPlugin>();
    }
}
