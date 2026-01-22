namespace Backend.Plugins.Harness;

public interface IPlugin
{
    IGlobalState GlobalState {
        get => GlobalStateStub.Instance;
        set => GlobalStateStub.Instance = value;
    }

    Task Execute();
}
