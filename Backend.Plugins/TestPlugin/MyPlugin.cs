namespace TestPlugin;

public class MyPlugin : PluginBase
{
    public override Channel[] InputChannels => [new Channel<PlayerInput>("Player Input")];

    public override Channel[] OutputChannels => [
        new Channel<PlayerOutput>("Player Output"),
        new Channel<OtherThing>("Other")
    ];
}
