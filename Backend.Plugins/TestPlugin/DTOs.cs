using System.Net;
using Plugins.Harness;

namespace TestPlugin;

public class State : IGlobalState
{
    public int PlayerId { get; set; }
    public string PlayerIP { get; set; }
}

public class PlayerInput
{
    public int Score { get; set; }
    public float Speed { get; set; }
    public string Name { get; set; } = "";
}

public class PlayerOutput
{
    public int Health { get; set; }
    public bool Alive { get; set; }
}

public record OtherThing(string Foo, decimal Bar);
