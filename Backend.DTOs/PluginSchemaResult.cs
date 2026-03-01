using System.Text.Json.Nodes;

namespace Backend.Models;
public sealed record PluginSchemaResult(JsonNode AggregateNode, SchemasFile? TypedSchemas, int DefinitionCount);
