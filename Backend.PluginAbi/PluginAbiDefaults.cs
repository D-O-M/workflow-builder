namespace Backend.PluginAbi;

public static class PluginAbiDefaults
{
    public const int CurrentMajor = 2;
    public const int CurrentMinor = 0;
    public const int CurrentInvocationEnvelopeSchemaVersion = 1;
    public const int CurrentResultSchemaVersion = 2;
    public const int CurrentHydrationProtocolVersion = 2;
    public const int CurrentProcessProtocolVersion = 1;
    public const string CurrentHarnessVersion = "2.0.0";

    public static PluginAbiVersion CurrentVersion { get; } = new(CurrentMajor, CurrentMinor);

    public static IReadOnlyList<string> CurrentSupportedStateStoreModes { get; } =
    [
        nameof(PluginStateStoreMode.Cosmos),
        nameof(PluginStateStoreMode.File)
    ];

    public static IReadOnlyList<string> CurrentSupportedPropertyKinds { get; } =
    [
        "String",
        "Integer",
        "Decimal",
        "Boolean",
        "Guid",
        "Date",
        "DateTime",
        "Enum"
    ];

    public static PluginAbiMetadata CurrentMetadata { get; } = new()
    {
        Version = CurrentVersion,
        InvocationEnvelopeSchemaVersion = CurrentInvocationEnvelopeSchemaVersion,
        ResultSchemaVersion = CurrentResultSchemaVersion,
        HydrationProtocolVersion = CurrentHydrationProtocolVersion,
        ProcessProtocolVersion = CurrentProcessProtocolVersion,
        HarnessVersion = CurrentHarnessVersion,
        SupportedStateStoreModes = CurrentSupportedStateStoreModes,
        SupportedPropertyKinds = CurrentSupportedPropertyKinds
    };
}
