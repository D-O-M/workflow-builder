using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.DTOs;
using Backend.Interfaces.Utilities;
using Microsoft.Extensions.Logging;

namespace Backend.Utilities;

public sealed class PluginSchemaAggregationUtility : IPluginSchemaAggregationUtility
{
    private readonly ILogger<PluginSchemaAggregationUtility> _logger;

    public PluginSchemaAggregationUtility(ILogger<PluginSchemaAggregationUtility> logger)
    {
        _logger = logger;
    }

    public async Task<PluginSchemaResult?> LoadAsync(string workspaceDirectory, CancellationToken cancellationToken)
    {
        var schemasJsonPath = Path.Combine(workspaceDirectory, "schemas.json");
        JsonNode? aggregateNode = null;

        if (File.Exists(schemasJsonPath))
        {
            await using var schemasStream = File.OpenRead(schemasJsonPath);
            aggregateNode = await JsonNode.ParseAsync(schemasStream, cancellationToken: cancellationToken);
            if (aggregateNode is null)
            {
                _logger.LogWarning("Unable to parse schemas.json at {SchemasPath}", schemasJsonPath);
                return null;
            }
        }
        else
        {
            var schemasDirectory = Path.Combine(workspaceDirectory, "schemas");
            if (!Directory.Exists(schemasDirectory))
            {
                _logger.LogInformation("No schema definitions found in workspace {Workspace}", workspaceDirectory);
                return null;
            }

            var components = new JsonObject();
            foreach (var file in Directory.EnumerateFiles(schemasDirectory, "*.json", SearchOption.AllDirectories))
            {
                await using var schemaStream = File.OpenRead(file);
                var node = await JsonNode.ParseAsync(schemaStream, cancellationToken: cancellationToken);
                if (node is not null)
                {
                    var key = Path.GetFileNameWithoutExtension(file);
                    components[key] = node;
                }
            }

            aggregateNode = new JsonObject
            {
                ["components"] = new JsonObject
                {
                    ["schemas"] = components
                }
            };
        }

        if (aggregateNode is null)
        {
            return null;
        }

        SchemasFile? typedSchemas = null;
        try
        {
            typedSchemas = aggregateNode.Deserialize<SchemasFile>(new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to bind schemas.json to SchemasFile, returning raw JsonNode instead.");
        }

        var definitionCount = CountDefinitions(aggregateNode);
        return new PluginSchemaResult(aggregateNode, typedSchemas, definitionCount);
    }

    private static int CountDefinitions(JsonNode? aggregateNode)
    {
        if (aggregateNode is JsonObject root &&
            root.TryGetPropertyValue("components", out var componentsNode) &&
            componentsNode is JsonObject components &&
            components.TryGetPropertyValue("schemas", out var schemasNode) &&
            schemasNode is JsonObject schemas)
        {
            return schemas.Count;
        }

        return 0;
    }
}
