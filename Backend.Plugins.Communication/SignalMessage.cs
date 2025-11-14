namespace Backend.Plugins.Communication;

/// <summary>
/// Logical payload exchanged through the duplex channel. Commands define the
/// semantics while the payload remains opaque to the channel itself.
/// </summary>
/// <param name="Command">The <see cref="SignalCommand"/> prefix.</param>
/// <param name="Payload">Opaque payload interpreted by the command owner.</param>
public readonly record struct SignalMessage(SignalCommand Command, ReadOnlyMemory<byte> Payload)
{
    public static SignalMessage Empty(SignalCommand command = SignalCommand.None)
        => new(command, ReadOnlyMemory<byte>.Empty);

    public int PayloadLength => Payload.Length;
}
