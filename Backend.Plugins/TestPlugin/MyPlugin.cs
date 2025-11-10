using Plugins.Harness;

namespace TestPlugin;

public class MyPlugin : IPlugin
{
    public PlayerInput PlayerInput { get; set; }

    public bool Success { get; set; }

    public Task Execute()
    {
        Success = true;
        return Task.CompletedTask;
    }
}
