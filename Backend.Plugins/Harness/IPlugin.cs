namespace Plugins.Harness;

public interface IPlugin
{
    IGlobalState GlobalState {
        get => GlobalStateStub.Instance;
        init => GlobalStateStub.Instance = value;
    }

    Task Execute();
}
