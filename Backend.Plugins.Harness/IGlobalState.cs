namespace Backend.Plugins.Harness;

public static class GlobalStateStub
{
    public static IGlobalState Instance {
        get => throw new InvalidOperationException();
        set => throw new InvalidOperationException();
    }
}

public interface IGlobalState
{
}
