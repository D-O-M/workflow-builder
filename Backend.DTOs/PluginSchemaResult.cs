using System.Text.Json.Nodes;

namespace Backend.DTOs;
public sealed record PluginSchemaResult(JsonNode AggregateNode, SchemasFile? TypedSchemas, int DefinitionCount);
