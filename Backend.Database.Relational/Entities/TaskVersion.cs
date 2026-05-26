namespace Backend.Database.Relational.Entities;

using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Backend.PluginAbi;

public class TaskVersion
{
    public int Id { get; set; }
    public string? Description { get; set; }

    public int TaskDefinitionId { get; set; }
    public TaskDefinition? TaskDefinition { get; set; }

    public required Version Version { get; set; }
    public required string Path { get; set; }
    public TaskVersionState State { get; set; } = TaskVersionState.Registered;
    public int AbiVersionMajor { get; set; } = PluginAbiDefaults.CurrentMajor;
    public int AbiVersionMinor { get; set; } = PluginAbiDefaults.CurrentMinor;
    public int AbiInvocationEnvelopeSchemaVersion { get; set; } = PluginAbiDefaults.CurrentInvocationEnvelopeSchemaVersion;
    public int AbiResultSchemaVersion { get; set; } = PluginAbiDefaults.CurrentResultSchemaVersion;
    public int AbiHydrationProtocolVersion { get; set; } = PluginAbiDefaults.CurrentHydrationProtocolVersion;
    public int AbiProcessProtocolVersion { get; set; } = PluginAbiDefaults.CurrentProcessProtocolVersion;
    public string AbiHarnessVersion { get; set; } = PluginAbiDefaults.CurrentHarnessVersion;
    public string AbiSupportedStateStoreModesJson { get; set; } =
        SerializeAbiList(PluginAbiDefaults.CurrentSupportedStateStoreModes);
    public string AbiSupportedPropertyKindsJson { get; set; } =
        SerializeAbiList(PluginAbiDefaults.CurrentSupportedPropertyKinds);

    public List<Channel> Channels { get; set; } = new();

    [NotMapped]
    public PluginAbiMetadata Abi
    {
        get => new()
        {
            Version = new PluginAbiVersion(AbiVersionMajor, AbiVersionMinor),
            InvocationEnvelopeSchemaVersion = AbiInvocationEnvelopeSchemaVersion,
            ResultSchemaVersion = AbiResultSchemaVersion,
            HydrationProtocolVersion = AbiHydrationProtocolVersion,
            ProcessProtocolVersion = AbiProcessProtocolVersion,
            HarnessVersion = AbiHarnessVersion,
            SupportedStateStoreModes = DeserializeAbiList(AbiSupportedStateStoreModesJson),
            SupportedPropertyKinds = DeserializeAbiList(AbiSupportedPropertyKindsJson)
        };
        set
        {
            AbiVersionMajor = value.Version.Major;
            AbiVersionMinor = value.Version.Minor;
            AbiInvocationEnvelopeSchemaVersion = value.InvocationEnvelopeSchemaVersion;
            AbiResultSchemaVersion = value.ResultSchemaVersion;
            AbiHydrationProtocolVersion = value.HydrationProtocolVersion;
            AbiProcessProtocolVersion = value.ProcessProtocolVersion;
            AbiHarnessVersion = value.HarnessVersion;
            AbiSupportedStateStoreModesJson = SerializeAbiList(value.SupportedStateStoreModes);
            AbiSupportedPropertyKindsJson = SerializeAbiList(value.SupportedPropertyKinds);
        }
    }

    [NotMapped]
    public IEnumerable<InputChannelDefinition> InputChannels => Channels.OfType<InputChannelDefinition>();

    [NotMapped]
    public IEnumerable<OutputChannelDefinition> OutputChannels => Channels.OfType<OutputChannelDefinition>();

    public static string SerializeAbiList(IEnumerable<string> values) =>
        JsonSerializer.Serialize(values.OrderBy(value => value, StringComparer.Ordinal).ToArray());

    public static IReadOnlyList<string> DeserializeAbiList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<string>>(json) ?? [];
    }
}
