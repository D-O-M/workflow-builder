using Backend.Plugins.Harness;
using TestPlugin;

var plugin = Plugin.CreateBuilder<MyPlugin>("A Test Plugin")
    .ConfigureInputChannel(p => p.PlayerInput)
    .ConfigureOutputChannel(p => p.Success)
    .Build();

await plugin.RunAsync();
