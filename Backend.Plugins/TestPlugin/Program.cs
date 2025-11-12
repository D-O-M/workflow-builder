using Plugins.Harness;
using TestPlugin;

var plugin = Plugin.CreateBuilder<MyPlugin>("A Test Plugin")
    .ConfigureInputChannel(p => p.PlayerInput)
    .ConfigureOutputChannel(p => p.Success)
    .WithGlobalState(p => p.State)
    .Build();

await plugin.RunAsync();
