using Backend.Plugins.Harness;
using WindowWeatherOutlook;

var plugin = Plugin.CreateBuilder<WindowWeatherOutlookPlugin>("Window weather outlook")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Weather)
    .Build();

await plugin.RunAsync();
