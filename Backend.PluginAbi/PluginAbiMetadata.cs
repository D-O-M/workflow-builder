namespace Backend.PluginAbi;

public sealed record PluginAbiMetadata
{
    public PluginAbiVersion Version { get; init; }
    public int InvocationEnvelopeSchemaVersion { get; init; }
    public int ResultSchemaVersion { get; init; }
    public int HydrationProtocolVersion { get; init; }
    public int ProcessProtocolVersion { get; init; }
    public string HarnessVersion { get; init; } = string.Empty;
    public IReadOnlyList<string> SupportedStateStoreModes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SupportedPropertyKinds { get; init; } = Array.Empty<string>();
}
