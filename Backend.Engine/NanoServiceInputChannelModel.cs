namespace Backend.Engine;

public sealed record NanoServiceInputChannelModel(
    string ChannelName,
    string ModelType,
    object Model);
