using Plugins.Harness;
using TestPlugin;

var plugin = Plugin.CreateBuilder<MyPlugin>()
    .ConfigureInputChannel(p => p.PlayerInput)
    .ConfigureOutputChannel(p => p.Success);
