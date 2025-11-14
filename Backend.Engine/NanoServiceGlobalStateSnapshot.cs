namespace Backend.Engine;

public sealed record NanoServiceGlobalStateSnapshot(
    string StateType,
    object State);
