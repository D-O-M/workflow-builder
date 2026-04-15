namespace Backend.Plugins.Harness;

public interface IPlugin
{
    public GlobalStateStub GlobalState {
        get => new GlobalStateStub();
        set => GlobalState = value;
    }

    Task Execute();
}
