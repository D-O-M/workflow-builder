namespace Backend.Plugins.Harness;

public class GlobalStateStub : IGlobalState
{
    public bool Empty { get; set; } = true;
}

public interface IGlobalState
{
}
