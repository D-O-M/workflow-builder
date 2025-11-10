using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Plugins.Harness;

public class PluginBuilder<TPlugin>
    where TPlugin : class, IPlugin
{
    private readonly Dictionary<string, Channel> _inputChannels = new();
    private readonly Dictionary<string, Channel> _outputChannels = new();
    private readonly HostApplicationBuilder _hostAppBuilder = Host.CreateApplicationBuilder();

    public IServiceCollection Services => _hostAppBuilder.Services;

    public PluginBuilder()
    {
        _hostAppBuilder.Logging.ClearProviders().AddSimpleConsole();
    }

    public PluginBuilder<TPlugin> CreateBuilder()
    {
        return this;
    }

    public PluginBuilder<TPlugin> ConfigureInputChannel<TChannelType>(Func<TPlugin, TChannelType> propertySelector)
    {
        if (_inputChannels.ContainsKey(nameof(propertySelector.Target)))
        {
            throw new InvalidOperationException($"Input channel with name '{nameof(propertySelector.Target)}' is already configured.");
        }

        _inputChannels.Add(nameof(propertySelector.Target),
            new Channel<TPlugin, TChannelType>(propertySelector));

        return this;
    }



    public PluginBuilder<TPlugin> ConfigureOutputChannel<TChannelType>(Func<TPlugin, TChannelType> propertySelector)
    {
        if (_outputChannels.ContainsKey(nameof(propertySelector.Target)))
        {
            throw new InvalidOperationException($"Output channel with name '{nameof(propertySelector.Target)}' is already configured.");
        }

        _outputChannels.Add(nameof(propertySelector.Target),
            new Channel<TPlugin, TChannelType>(propertySelector));

        return this;
    }
}
