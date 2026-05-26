using Backend.Plugins.Harness;

namespace TestPlugin;

public class MyPlugin : IPlugin
{
    public required PlayerInput PlayerInput { get; init; }

    public PlayerOutput? Success { get; set; } = null;

    public Task Execute()
    {
        Success = new PlayerOutput
        {
            Health = PlayerInput.Score * 2,
            Alive = PlayerInput.Speed > 5.0f
        };

        return Task.CompletedTask;
    }
}
