using Backend.Plugins.Harness;

namespace TestPlugin;

[Backend.Plugins.Harness.PluginJsonSerializable]
public class State : IGlobalState
{
    public int PlayerId { get; set; }
    public string PlayerIP { get; set; }
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public class PlayerInput
{
    public int Score { get; set; }
    public float Speed { get; set; }
    public string Name { get; set; } = "";
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public class PlayerOutput
{
    public int Health { get; set; }
    public bool Alive { get; set; }
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public record OtherThing(string Foo, decimal Bar);
