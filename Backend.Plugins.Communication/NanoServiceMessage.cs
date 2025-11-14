namespace Backend.Plugins.Communication;

/// <summary>
/// Represents the logical payload exchanged between the engine and a nano service.
/// </summary>
/// <param name="Command">The <see cref="NanoServiceCommand"/> prefix.</param>
/// <param name="Payload">Opaque payload data interpreted by the command owner.</param>
public readonly record struct NanoServiceMessage(NanoServiceCommand Command, ReadOnlyMemory<byte> Payload)
{
    public static NanoServiceMessage Empty(NanoServiceCommand command = NanoServiceCommand.None)
        => new(command, ReadOnlyMemory<byte>.Empty);

    public int PayloadLength => Payload.Length;
}
