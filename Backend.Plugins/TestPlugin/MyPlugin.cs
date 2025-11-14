using Backend.Plugins.Harness;

namespace TestPlugin;

public class MyPlugin : IPlugin
{
    public required State State { get; set; }
    public required PlayerInput PlayerInput { get; init; }

    public bool Success { get; set; }

    public Task Execute()
    {
        Success = true;
        return Task.CompletedTask;
    }
}
