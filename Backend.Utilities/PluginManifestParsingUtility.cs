using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Models;
using Backend.Interfaces.Utilities;
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
            InputChannel = ReadChannel(manifestObject["inputChannel"]),
            OutputChannels = ReadChannels(manifestObject["outputChannels"]),
            GlobalState = ReadGlobalState(manifestObject["globalState"])
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
}
