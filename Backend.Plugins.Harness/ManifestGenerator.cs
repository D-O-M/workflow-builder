using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

namespace Backend.Plugins.Harness;

public static class ManifestGenerator
{
    private static readonly JsonSerializerOptions _jsonSerializerOptions =
        new()
        {
            RespectNullableAnnotations = true,
            WriteIndented = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };

    /// <summary>
    /// Build a PluginManifest from the supplied channels and optional global state type.
    /// </summary>
    public static PluginManifest CreateManifest(
        string displayName,
        IEnumerable<KeyValuePair<string, Type>> inputChannels,
        IEnumerable<KeyValuePair<string, Type>> outputChannels,
        Type? globalStateType)
    {
        return new PluginManifest
        {
            DisplayName = displayName,
            InputChannels = inputChannels
                .Select(kv => new ChannelManifest { Name = kv.Key, Type = kv.Value.FullName ?? kv.Value.Name })
                .ToList(),
            OutputChannels = outputChannels
                .Select(kv => new ChannelManifest { Name = kv.Key, Type = kv.Value.FullName ?? kv.Value.Name })
                .ToList(),
            GlobalState = (globalStateType ?? typeof(IGlobalState)).FullName
        };
    }

    /// <summary>
    /// Emit manifest.json and JSON Schemas (one file per complex root type + aggregated schemas.json).
    /// Returns (manifestPath, schemaDir, aggregatedSchemasPath, schemaCount).
    /// Skips simple scalar models (bools, numbers, strings, DateTime, etc.). Enums are included by default.
    /// </summary>
    public static (string manifestPath, string schemaDir, string? aggregatedSchemasPath, int schemaCount)
        EmitAll(
            string projectDir,
            PluginManifest manifest,
            IEnumerable<Type> rootTypes,
            string manifestFileName = "manifest.json",
            string perTypeSchemaDirName = "schemas",
            string aggregatedSchemasFileName = "schemas.json",
            bool includeEnums = true)
    {
        Directory.CreateDirectory(projectDir);

        // 1) manifest.json (write only if changed)
        var manifestJson = JsonSerializer.Serialize(manifest, _jsonSerializerOptions);
        var manifestPath = Path.Combine(projectDir, manifestFileName);
        WriteIfChanged(manifestPath, manifestJson);

        // Build filtered root type set (no scalars; unwrap nullable; use element type for collections)
        var filteredRoots = ExpandComplexRoots(rootTypes, includeEnums).ToArray();

        // 2) per-type schemas using .NET 9 JsonSchemaExporter
        var schemaDir = Path.Combine(projectDir, perTypeSchemaDirName);
        Directory.CreateDirectory(schemaDir);

        var exporterOptions = new JsonSchemaExporterOptions();

        var schemaCount = 0;
        foreach (var type in filteredRoots)
        {
            JsonNode schemaNode = JsonSchemaExporter.GetJsonSchemaAsNode(_jsonSerializerOptions, type, exporterOptions);
            var fileSafeName = MakeFileSafe(type.FullName ?? type.Name) + ".schema.json";
            var path = Path.Combine(schemaDir, fileSafeName);
            WriteIfChanged(path, schemaNode.ToJsonString(_jsonSerializerOptions));
            schemaCount++;
        }

        // 3) aggregated schemas.json (compose under components.schemas)
        string? aggregatedPath = null;
        if (filteredRoots.Length > 0)
        {
            var components = new JsonObject();
            foreach (var type in filteredRoots.OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                var node = JsonSchemaExporter.GetJsonSchemaAsNode(_jsonSerializerOptions, type, exporterOptions);
                components[type.FullName ?? type.Name] = node;
            }

            var aggregate = new JsonObject
            {
                ["components"] = new JsonObject
                {
                    ["schemas"] = components
                }
            };

            var aggJson = aggregate.ToJsonString(_jsonSerializerOptions);
            aggregatedPath = Path.Combine(projectDir, aggregatedSchemasFileName);
            WriteIfChanged(aggregatedPath, aggJson);
        }

        return (manifestPath, schemaDir, aggregatedPath, schemaCount);
    }

    // ---------------- helpers ----------------

    /// <summary>
    /// Expands the provided root types into a unique set of complex types by:
    /// - unwrapping Nullable&lt;T&gt;
    /// - using element type for IEnumerable&lt;T&gt;/arrays
    /// - skipping simple scalars (bool, numbers, string, DateTime, Guid, etc.)
    /// - including or skipping enums based on <paramref name="includeEnums"/>
    /// </summary>
    public static IEnumerable<Type> ExpandComplexRoots(IEnumerable<Type> roots, bool includeEnums)
    {
        var seen = new HashSet<Type>();
        foreach (var t0 in roots)
        {
            if (t0 is null) continue;

            var t = UnwrapNullable(t0);

            // If the channel is a collection, use its element type
            if (TryGetEnumerableElementType(t, out var elem))
                t = UnwrapNullable(elem);

            // Skip simple scalars (optionally skip enums too)
            if (IsSimpleScalar(t, includeEnums))
                continue;

            if (seen.Add(t))
                yield return t;
        }
    }

    private static Type UnwrapNullable(Type t) => Nullable.GetUnderlyingType(t) ?? t;

    private static bool TryGetEnumerableElementType(Type t, out Type elementType)
    {
        // arrays
        if (t.IsArray)
        {
            elementType = t.GetElementType()!;
            return true;
        }

        // IEnumerable<T> (covers List<T>, HashSet<T>, etc.)
        var ienumT = t.GetInterfaces()
                      .Concat(t.IsInterface ? new[] { t } : Array.Empty<Type>())
                      .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        if (ienumT is not null)
        {
            elementType = ienumT.GetGenericArguments()[0];
            return true;
        }

        elementType = null!;
        return false;
    }

    private static bool IsSimpleScalar(Type t, bool includeEnums)
    {
        // If includeEnums is true, enums are NOT treated as simple (so we generate schemas for them).
        if (t.IsEnum) return !includeEnums;

        if (t.IsPrimitive) return true;

        // Common scalar CLR types that serialize to a single JSON token
        return t == typeof(string)
            || t == typeof(decimal)
            || t == typeof(DateTime)
            || t == typeof(DateOnly)
            || t == typeof(TimeOnly)
            || t == typeof(DateTimeOffset)
            || t == typeof(Guid)
            || t == typeof(Uri)
            || t == typeof(TimeSpan);
    }

    public static void WriteIfChanged(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path);
            if (JsonEquals(existing, content)) return;
        }
        File.WriteAllText(path, content);
    }

    private static bool JsonEquals(string a, string b)
    {
        return a == b;
    }

    private static string MakeFileSafe(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
