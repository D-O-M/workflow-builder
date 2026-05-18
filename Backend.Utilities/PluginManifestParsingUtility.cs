using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Models;
using Backend.Interfaces.Utilities;
using Backend.PluginAbi;
using Microsoft.Extensions.Logging;

namespace Backend.Utilities;

public sealed class PluginManifestParsingUtility : IPluginManifestParsingUtility
{
    private readonly ILogger<PluginManifestParsingUtility> _logger;

    public PluginManifestParsingUtility(ILogger<PluginManifestParsingUtility> logger)
    {
        _logger = logger;
    }

    public async Task<ManifestFile?> ReadAsync(string workspaceDirectory, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(workspaceDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            _logger.LogWarning("No manifest.json found in workspace {Workspace}", workspaceDirectory);
            return null;
        }

        await using var manifestStream = File.OpenRead(manifestPath);
        var manifestNode = await JsonNode.ParseAsync(manifestStream, cancellationToken: cancellationToken);
        if (manifestNode is not JsonObject manifestObject)
        {
            _logger.LogWarning("Unable to parse manifest at {ManifestPath}", manifestPath);
            return null;
        }

        return new ManifestFile
        {
            DisplayName = manifestObject["displayName"]?.GetValue<string>()
                ?? throw new JsonException("Manifest displayName is missing."),
            Abi = ReadAbi(manifestObject["abi"]),
            InputChannel = ReadChannel(manifestObject["inputChannel"]),
            OutputChannels = ReadChannels(manifestObject["outputChannels"]),
            GlobalState = ReadGlobalState(manifestObject["globalState"]),
            Configuration = ReadConfiguration(manifestObject["configuration"]),
            Capabilities = ReadCapabilities(manifestObject["capabilities"])
        };
    }

    private static PluginAbiMetadata? ReadAbi(JsonNode? node)
    {
        if (node is not JsonObject abiObject)
        {
            return null;
        }

        var versionObject = abiObject["version"] as JsonObject
            ?? throw new JsonException("Manifest abi.version is missing.");

        return new PluginAbiMetadata
        {
            Version = new PluginAbiVersion(
                versionObject["major"]?.GetValue<int>()
                    ?? throw new JsonException("Manifest abi.version.major is missing."),
                versionObject["minor"]?.GetValue<int>()
                    ?? throw new JsonException("Manifest abi.version.minor is missing.")),
            InvocationEnvelopeSchemaVersion = abiObject["invocationEnvelopeSchemaVersion"]?.GetValue<int>()
                ?? throw new JsonException("Manifest abi.invocationEnvelopeSchemaVersion is missing."),
            ResultSchemaVersion = abiObject["resultSchemaVersion"]?.GetValue<int>()
                ?? throw new JsonException("Manifest abi.resultSchemaVersion is missing."),
            HydrationProtocolVersion = abiObject["hydrationProtocolVersion"]?.GetValue<int>()
                ?? throw new JsonException("Manifest abi.hydrationProtocolVersion is missing."),
            HarnessVersion = abiObject["harnessVersion"]?.GetValue<string>()
                ?? throw new JsonException("Manifest abi.harnessVersion is missing."),
            SupportedStateStoreModes = ReadStringArray(abiObject["supportedStateStoreModes"]),
            SupportedPropertyKinds = ReadStringArray(abiObject["supportedPropertyKinds"])
        };
    }

    private static ManifestFileChannelInfo? ReadGlobalState(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var legacyTypeName))
        {
            return new ManifestFileChannelInfo
            {
                Name = "GlobalState",
                Type = legacyTypeName,
                Properties = []
            };
        }

        return ReadChannel(node);
    }

    private static List<ManifestFileChannelInfo> ReadChannels(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        return array
            .Select(ReadChannel)
            .Where(channel => channel is not null)
            .Cast<ManifestFileChannelInfo>()
            .ToList();
    }

    private static ManifestFileChannelInfo? ReadChannel(JsonNode? node)
    {
        if (node is not JsonObject channelObject)
        {
            return null;
        }

        return new ManifestFileChannelInfo
        {
            Name = channelObject["name"]?.GetValue<string>()
                ?? throw new JsonException("Manifest channel name is missing."),
            Type = channelObject["type"]?.GetValue<string>()
                ?? throw new JsonException("Manifest channel type is missing."),
            Properties = ReadProperties(channelObject["properties"])
        };
    }

    private static List<ManifestFilePropertyInfo> ReadProperties(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        return array
            .OfType<JsonObject>()
            .Select(propertyObject => new ManifestFilePropertyInfo
            {
                Name = propertyObject["name"]?.GetValue<string>()
                    ?? throw new JsonException("Manifest property name is missing."),
                Kind = propertyObject["kind"]?.GetValue<string>()
                    ?? throw new JsonException("Manifest property kind is missing."),
                Required = propertyObject["required"]?.GetValue<bool>() ?? false,
                EnumValues = propertyObject["enumValues"] is JsonArray enumArray
                    ? enumArray
                        .Select(value => value?.GetValue<string>())
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Cast<string>()
                        .ToList()
                    : []
            })
            .ToList();
    }

    private static ManifestFileConfigurationInfo ReadConfiguration(JsonNode? node)
    {
        if (node is not JsonObject configurationObject)
        {
            return new ManifestFileConfigurationInfo();
        }

        return new ManifestFileConfigurationInfo
        {
            Requirements = ReadConfigurationRequirements(configurationObject["requirements"])
        };
    }

    private static List<ManifestFileConfigurationRequirementInfo> ReadConfigurationRequirements(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        return array
            .OfType<JsonObject>()
            .Select(requirementObject => new ManifestFileConfigurationRequirementInfo
            {
                DomainId = requirementObject["domainId"]?.GetValue<string>()
                    ?? throw new JsonException("Manifest configuration requirement domainId is missing."),
                SourceCapabilityId = requirementObject["sourceCapabilityId"]?.GetValue<string>() ?? "",
                SourceCapabilityType = requirementObject["sourceCapabilityType"]?.GetValue<string>() ?? "",
                OptionsType = requirementObject["optionsType"]?.GetValue<string>()
                    ?? throw new JsonException("Manifest configuration requirement optionsType is missing."),
                OptionsName = requirementObject["optionsName"]?.GetValue<string>()
                    ?? throw new JsonException("Manifest configuration requirement optionsName is missing."),
                Key = requirementObject["key"]?.GetValue<string>()
                    ?? throw new JsonException("Manifest configuration requirement key is missing."),
                PropertyPath = requirementObject["propertyPath"]?.GetValue<string>()
                    ?? throw new JsonException("Manifest configuration requirement propertyPath is missing."),
                ClrType = requirementObject["clrType"]?.GetValue<string>()
                    ?? throw new JsonException("Manifest configuration requirement clrType is missing."),
                Kind = requirementObject["kind"]?.GetValue<string>()
                    ?? throw new JsonException("Manifest configuration requirement kind is missing."),
                Required = requirementObject["required"]?.GetValue<bool>() ?? false,
                Secret = requirementObject["secret"]?.GetValue<bool>() ?? false,
                Description = requirementObject["description"]?.GetValue<string>(),
                IsCollection = requirementObject["isCollection"]?.GetValue<bool>() ?? false,
                EnumValues = requirementObject["enumValues"] is JsonArray enumArray
                    ? enumArray
                        .Select(value => value?.GetValue<string>())
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Cast<string>()
                        .ToList()
                    : []
            })
            .ToList();
    }

    private static ManifestFileCapabilitiesInfo ReadCapabilities(JsonNode? node)
    {
        if (node is not JsonObject capabilitiesObject)
        {
            return new ManifestFileCapabilitiesInfo();
        }

        return new ManifestFileCapabilitiesInfo
        {
            Requirements = ReadCapabilityRequirements(capabilitiesObject["requirements"])
        };
    }

    private static List<ManifestFileCapabilityRequirementInfo> ReadCapabilityRequirements(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        return array
            .OfType<JsonObject>()
            .Select(requirementObject => new ManifestFileCapabilityRequirementInfo
            {
                Id = requirementObject["id"]?.GetValue<string>()
                    ?? throw new JsonException("Manifest capability requirement id is missing."),
                Kind = requirementObject["kind"]?.GetValue<string>()
                    ?? throw new JsonException("Manifest capability requirement kind is missing."),
                CapabilityType = requirementObject["capabilityType"]?.GetValue<string>()
                    ?? throw new JsonException("Manifest capability requirement capabilityType is missing."),
                DisplayName = requirementObject["displayName"]?.GetValue<string>()
                    ?? throw new JsonException("Manifest capability requirement displayName is missing."),
                Description = requirementObject["description"]?.GetValue<string>(),
                Required = requirementObject["required"]?.GetValue<bool>() ?? true,
                Installable = requirementObject["installable"]?.GetValue<bool>() ?? false,
                Remediation = requirementObject["remediation"]?.GetValue<string>(),
                Command = requirementObject["command"]?.GetValue<string>(),
                Arguments = requirementObject["arguments"]?.GetValue<string>(),
                MinimumVersion = requirementObject["minimumVersion"]?.GetValue<string>(),
                EndpointConfigKey = requirementObject["endpointConfigKey"]?.GetValue<string>(),
                TimeoutSeconds = requirementObject["timeoutSeconds"]?.GetValue<int>(),
                ConfigurationKeys = ReadStringArray(requirementObject["configurationKeys"]),
                EngineConfigurationKeys = ReadStringArray(requirementObject["engineConfigurationKeys"])
            })
            .ToList();
    }

    private static List<string> ReadStringArray(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        return array
            .Select(value => value?.GetValue<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToList();
    }
}
