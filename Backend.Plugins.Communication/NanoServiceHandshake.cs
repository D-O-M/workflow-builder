namespace Backend.Plugins.Communication;

/// <summary>
/// Carries the metadata exchanged over STDIO before both parties switch to
/// the memory-mapped protocol.
/// </summary>
/// <param name="ProtocolVersion">Version identifier for compatibility checks.</param>
/// <param name="SessionId">Correlation id shared by both participants.</param>
/// <param name="EngineToNanoMapName">Ring buffer used for commands flowing from the engine to the nano service.</param>
/// <param name="NanoToEngineMapName">Ring buffer used for commands flowing from the nano service to the engine.</param>
/// <param name="EngineToNanoCapacity">Byte capacity of the engine-to-nano buffer.</param>
/// <param name="NanoToEngineCapacity">Byte capacity of the nano-to-engine buffer.</param>
/// <param name="EngineToNanoMutexName">Cross-process mutex protecting the engine-to-nano ring.</param>
/// <param name="NanoToEngineMutexName">Cross-process mutex protecting the nano-to-engine ring.</param>
/// <param name="EngineProcessId">Process id of the orchestrating engine.</param>
/// <param name="CreatedUtc">Timestamp to aid diagnostics.</param>
public sealed record NanoServiceHandshake(
    byte ProtocolVersion,
    Guid SessionId,
    string EngineToNanoMapName,
    string NanoToEngineMapName,
    int EngineToNanoCapacity,
    int NanoToEngineCapacity,
    string EngineToNanoMutexName,
    string NanoToEngineMutexName,
    int EngineProcessId,
    DateTimeOffset CreatedUtc)
{
    public const byte CurrentProtocolVersion = 1;
}
