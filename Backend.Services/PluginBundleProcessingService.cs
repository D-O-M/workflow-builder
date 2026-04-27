using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Backend.Database.Relational.Entities;
using Backend.Interfaces.Queues;
using Backend.Models;
using Backend.Models.DomainModels;
using Backend.Interfaces.Repositories;
using Backend.Interfaces.Utilities;
using Backend.Plugins.Communication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
namespace Backend.Services;

public sealed class PluginBundleProcessingService : IPluginBundleProcessingService
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _pluginProcessingGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly IPluginBundleSource _pluginBundleSource;
    private readonly IPluginWorkspaceFactoryUtility _workspaceFactory;
    private readonly IPluginManifestParsingUtility _manifestParser;
    private readonly IPluginSchemaAggregationUtility _schemaAggregator;
    private readonly IPluginReloadQueueWriter _pluginReloadQueueWriter;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PluginBundleProcessingService> _logger;

    public PluginBundleProcessingService(
        IPluginBundleSource pluginBundleSource,
        IPluginWorkspaceFactoryUtility workspaceFactory,
        IPluginManifestParsingUtility manifestReader,
        IPluginSchemaAggregationUtility schemaAggregator,
        IPluginReloadQueueWriter pluginReloadQueueWriter,
        IServiceScopeFactory scopeFactory,
        ILogger<PluginBundleProcessingService> logger)
    {
        _pluginBundleSource = pluginBundleSource;
        _workspaceFactory = workspaceFactory;
        _manifestParser = manifestReader;
        _schemaAggregator = schemaAggregator;
        _pluginReloadQueueWriter = pluginReloadQueueWriter;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task ProcessAsync(PluginBlobInfo pluginInfo, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing plugin {Plugin} ({Version}) from blob {BlobName}", pluginInfo.PluginName, pluginInfo.Version, pluginInfo.BlobName);

        var pluginGate = _pluginProcessingGates.GetOrAdd(
            pluginInfo.PluginName,
            static _ => new SemaphoreSlim(1, 1));

        await pluginGate.WaitAsync(cancellationToken);
        try
        {
            using var serviceScope = _scopeFactory.CreateScope();
            var taskDefinitionRepository = serviceScope.ServiceProvider.GetRequiredService<ITaskDefinitionRepository>();
            var existingTasks = await taskDefinitionRepository.GetAll(cancellationToken);
            var alreadyRegistered = existingTasks.Any(task =>
                task.Name == pluginInfo.PluginName &&
                task.Version == pluginInfo.Version);

            if (alreadyRegistered)
            {
                _logger.LogInformation(
                    "Plugin {Plugin} ({Version}) already registered; skipping processing.",
                    pluginInfo.PluginName,
                    pluginInfo.Version);
                return;
            }

            var workspaceDirectory = await _workspaceFactory.CreateWorkspaceAsync(pluginInfo.PluginName, pluginInfo.Version, cancellationToken);

            await _pluginBundleSource.ExtractBundleAsync(pluginInfo, workspaceDirectory, cancellationToken);

            var manifest = await _manifestParser.ReadAsync(workspaceDirectory, cancellationToken);
            if (manifest is null)
            {
                _logger.LogWarning("Manifest not found for plugin {Plugin} ({Version}). Processing aborted.", pluginInfo.PluginName, pluginInfo.Version);
                return;
            }

            var schemas = await _schemaAggregator.LoadAsync(workspaceDirectory, cancellationToken);
            LogProcessingSummary(pluginInfo, manifest, schemas);

            if (schemas is null) throw new Exception();

            var entryPoint = FindEntryPoint(workspaceDirectory, pluginInfo.PluginName);

            var version = new TaskVersionDomainModel
            {
                Name = pluginInfo.PluginName,
                DisplayName = manifest.DisplayName,
                Version = pluginInfo.Version,
                WorkspaceEntryPointPath = entryPoint,
                RequiredGlobalState = GetGlobalStateRequirements(pluginInfo, manifest, schemas),
                InputChannelDefinition = GetInputChannelDefinition(pluginInfo, manifest, schemas),
                OutputChannelDefinitions = GetOutputChannelDefinitions(pluginInfo, manifest, schemas)
            };

            try
            {
                var registrationResult = await taskDefinitionRepository.CreateVersion(version, cancellationToken);
                await _pluginReloadQueueWriter.EnqueueAsync(
                    new PluginReloadRequestMessage
                    {
                        TaskDefinitionId = registrationResult.TaskDefinitionId,
                        TaskVersionId = registrationResult.TaskVersionId,
                        TaskName = registrationResult.TaskName,
                        Version = registrationResult.Version.ToString(),
                        ChangeKind = registrationResult.ChangeKind.ToString(),
                        RequestedAt = DateTimeOffset.UtcNow
                    },
                    cancellationToken);
            }
            catch (DbUpdateException ex) when (IsDuplicateRegistrationRace(ex))
            {
                _logger.LogInformation(
                    "Plugin {Plugin} ({Version}) was registered by another concurrent detector invocation; skipping duplicate registration.",
                    pluginInfo.PluginName,
                    pluginInfo.Version);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to create task definition version for plugin {Plugin} ({Version}).",
                    pluginInfo.PluginName, pluginInfo.Version);
            }
        }
        finally
        {
            pluginGate.Release();
        }
    }

    private void LogProcessingSummary(PluginBlobInfo pluginInfo, ManifestFile manifest, PluginSchemaResult? schemas)
    {
        _logger.LogInformation(
            "Manifest parsed for plugin {Plugin} ({Version}). Inputs: {InputCount}, Outputs: {OutputCount}.",
            pluginInfo.PluginName,
            pluginInfo.Version,
            manifest.InputChannel is not null ? 1 : 0,
            manifest.OutputChannels?.Count ?? 0);

        if (schemas is null)
        {
            _logger.LogInformation("No schema information available for plugin {Plugin} ({Version}).", pluginInfo.PluginName, pluginInfo.Version);
            return;
        }

        _logger.LogInformation(
            "Loaded {SchemaCount} schema definition(s) for plugin {Plugin} ({Version}).",
            schemas.DefinitionCount,
            pluginInfo.PluginName,
            pluginInfo.Version);
    }

    private ChannelDefinitionDomainModel? GetInputChannelDefinition(
        PluginBlobInfo pluginInfo, ManifestFile manifest, PluginSchemaResult schemas)
    {
        if (manifest.InputChannel is null)
        {
            _logger.LogInformation(
                "Plugin {Plugin} ({Version}) did not declare an input channel.",
                pluginInfo.PluginName,
                pluginInfo.Version);
            return null;
        }

        var inputChannelType = manifest.InputChannel.Type;
        var propDefs = BuildPropertyDefinitions(
            pluginInfo,
            manifest.InputChannel,
            schemas,
            "input channel");

        return new ChannelDefinitionDomainModel
        {
            Name = manifest.InputChannel.Name,
            ModelTypeName = inputChannelType,
            Properties = propDefs
        };
    }

    private HashSet<ChannelDefinitionDomainModel> GetOutputChannelDefinitions(
        PluginBlobInfo pluginInfo, ManifestFile manifest, PluginSchemaResult schemas)
    {
        var channelDefs = new HashSet<ChannelDefinitionDomainModel>();

        foreach (var channel in manifest.OutputChannels)
        {
            var channelType = channel.Type;
            var propDefs = BuildPropertyDefinitions(
                pluginInfo,
                channel,
                schemas,
                $"output channel {channel.Name}");

            if (!channelDefs.Add(new ChannelDefinitionDomainModel
            {
                Name = channel.Name,
                ModelTypeName = channelType,
                Properties = propDefs
            }))
            {
                _logger.LogWarning("Encountered more than one identical channel named {ChannelName} while parsing " +
                    "the Output Channel definitions of version {PluginVersion} of plugin {PluginName}.",
                    channel.Name, pluginInfo.Version.ToString(), pluginInfo.PluginName);
            }
        }

        return channelDefs;
    }

    private HashSet<PropertyDefinitionDomainModel>? GetGlobalStateRequirements(
        PluginBlobInfo pluginInfo, ManifestFile manifest, PluginSchemaResult schemas)
    {
        var globalState = manifest.GlobalState;
        if (globalState is null || string.IsNullOrWhiteSpace(globalState.Type))
        {
            _logger.LogInformation(
                "Plugin {Plugin} ({Version}) did not declare a global state schema.",
                pluginInfo.PluginName,
                pluginInfo.Version);
            return null;
        }

        if (IsLegacyGlobalStateStub(globalState, schemas))
        {
            _logger.LogInformation(
                "Plugin {Plugin} ({Version}) declared the legacy GlobalStateStub schema; treating it as no global state.",
                pluginInfo.PluginName,
                pluginInfo.Version);
            return null;
        }

        return BuildPropertyDefinitions(
            pluginInfo,
            globalState,
            schemas,
            "global state");
    }

    private static bool IsLegacyGlobalStateStub(ManifestFileChannelInfo globalState, PluginSchemaResult schemas)
    {
        if (!string.Equals(globalState.Type, "Backend.Plugins.Harness.GlobalStateStub", StringComparison.Ordinal))
        {
            return false;
        }

        var manifestProperties = globalState.Properties;
        if (manifestProperties.Count == 1 &&
            string.Equals(manifestProperties[0].Name, "Empty", StringComparison.Ordinal) &&
            string.Equals(manifestProperties[0].Kind, WorkflowPropertyKind.Boolean.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (manifestProperties.Count > 0)
        {
            return false;
        }

        if (schemas.TypedSchemas?.Components?.Schemas is not null &&
            schemas.TypedSchemas.Components.Schemas.TryGetValue(globalState.Type, out var typedSchema) &&
            typedSchema?.Properties is not null)
        {
            return typedSchema.Properties.Count == 1 &&
                   typedSchema.Properties.TryGetValue("Empty", out var property) &&
                   ParseSchemaPropertyKind(property.Type, property.Format) == WorkflowPropertyKind.Boolean;
        }

        return false;
    }

    private HashSet<PropertyDefinitionDomainModel> BuildPropertyDefinitions(
        PluginBlobInfo pluginInfo,
        ManifestFileChannelInfo manifestChannel,
        PluginSchemaResult schemas,
        string schemaPurpose)
    {
        var propDefs = new HashSet<PropertyDefinitionDomainModel>();

        foreach (var property in GetDeclaredProperties(pluginInfo, manifestChannel, schemas, schemaPurpose))
        {
            if (!propDefs.Add(property))
            {
                _logger.LogWarning(
                    "Encountered more than one property named {PropertyName} while parsing the {SchemaPurpose} definition for plugin {PluginName} ({PluginVersion}).",
                    property.Name,
                    schemaPurpose,
                    pluginInfo.PluginName,
                    pluginInfo.Version);
            }
        }

        return propDefs;
    }

    private IReadOnlyCollection<PropertyDefinitionDomainModel> GetDeclaredProperties(
        PluginBlobInfo pluginInfo,
        ManifestFileChannelInfo manifestChannel,
        PluginSchemaResult schemas,
        string schemaPurpose)
    {
        if (manifestChannel.Properties.Count > 0)
        {
            return manifestChannel.Properties
                .Select(property => new PropertyDefinitionDomainModel
                {
                    Name = property.Name,
                    Kind = ParseManifestPropertyKind(property.Kind),
                    IsRequired = property.Required,
                    EnumValues = property.EnumValues
                })
                .ToArray();
        }

        return GetSchemaProperties(pluginInfo, schemas, manifestChannel.Type, schemaPurpose)
            .Select(property => new PropertyDefinitionDomainModel
            {
                Name = property.Key,
                Kind = property.Value.Kind,
                IsRequired = property.Value.IsRequired,
                EnumValues = property.Value.EnumValues
            })
            .ToArray();
    }

    private IReadOnlyDictionary<string, SchemaPropertyInfo> GetSchemaProperties(
        PluginBlobInfo pluginInfo,
        PluginSchemaResult schemas,
        string schemaType,
        string schemaPurpose)
    {
        if (schemas.TypedSchemas?.Components?.Schemas is not null &&
            schemas.TypedSchemas.Components.Schemas.TryGetValue(schemaType, out var typedSchema) &&
            typedSchema?.Properties is not null)
        {
            return typedSchema.Properties.ToDictionary(
                property => property.Key,
                property => new SchemaPropertyInfo(
                    ParseSchemaPropertyKind(property.Value.Type, property.Value.Format),
                    property.Value.Type.All(typeName => !string.Equals(typeName, "null", StringComparison.OrdinalIgnoreCase)),
                    property.Value.Enum ?? []),
                StringComparer.Ordinal);
        }

        if (TryGetRawSchemaProperties(schemas.AggregateNode, schemaType, out var rawProperties))
        {
            _logger.LogDebug(
                "Using raw schema parsing for {SchemaPurpose} {SchemaType} of plugin {PluginName} ({PluginVersion}).",
                schemaPurpose,
                schemaType,
                pluginInfo.PluginName,
                pluginInfo.Version);
            return rawProperties;
        }

        _logger.LogWarning(
            "Schema {SchemaType} for the {SchemaPurpose} of plugin {PluginName} ({PluginVersion}) could not be resolved. Continuing with no declared properties.",
            schemaType,
            schemaPurpose,
            pluginInfo.PluginName,
            pluginInfo.Version);
        return new Dictionary<string, SchemaPropertyInfo>(StringComparer.Ordinal);
    }

    private static bool TryGetRawSchemaProperties(
        JsonNode aggregateNode,
        string schemaType,
        out IReadOnlyDictionary<string, SchemaPropertyInfo> properties)
    {
        properties = new Dictionary<string, SchemaPropertyInfo>(StringComparer.Ordinal);

        if (aggregateNode is not JsonObject root ||
            root["components"] is not JsonObject components ||
            components["schemas"] is not JsonObject schemas ||
            schemas[schemaType] is not JsonObject schemaObject)
        {
            return false;
        }

        if (schemaObject["properties"] is not JsonObject propertyNodes)
        {
            properties = new Dictionary<string, SchemaPropertyInfo>(StringComparer.Ordinal);
            return true;
        }

        var parsedProperties = new Dictionary<string, SchemaPropertyInfo>(StringComparer.Ordinal);
        foreach (var propertyNode in propertyNodes)
        {
            var propertyType = ResolvePropertyType(propertyNode.Value);
            if (propertyType is null)
            {
                continue;
            }

            parsedProperties[propertyNode.Key] = propertyType;
        }

        properties = parsedProperties;
        return true;
    }

    private static SchemaPropertyInfo? ResolvePropertyType(JsonNode? propertyNode)
    {
        if (propertyNode is not JsonObject propertyObject)
        {
            return null;
        }

        var typeNames = ReadTypeNames(propertyObject["type"]);
        if (typeNames.Count == 0)
        {
            return null;
        }

        var format = propertyObject["format"]?.GetValue<string>();
        var enumValues = propertyObject["enum"] is JsonArray enumArray
            ? enumArray
                .Select(value => value?.GetValue<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray()
            : [];

        return new SchemaPropertyInfo(
            ParseSchemaPropertyKind(typeNames, format),
            typeNames.All(typeName => !string.Equals(typeName, "null", StringComparison.OrdinalIgnoreCase)),
            enumValues);
    }

    private static IReadOnlyList<string> ReadTypeNames(JsonNode? node)
    {
        if (node is JsonValue stringValue &&
            stringValue.TryGetValue<string>(out var scalarType) &&
            !string.IsNullOrWhiteSpace(scalarType))
        {
            return [scalarType];
        }

        if (node is not JsonArray typeArray)
        {
            return [];
        }

        return typeArray
            .Select(candidate => candidate?.GetValue<string>())
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Cast<string>()
            .ToArray();
    }

    private static WorkflowPropertyKind ParseManifestPropertyKind(string kind) =>
        Enum.TryParse<WorkflowPropertyKind>(kind, ignoreCase: true, out var parsedKind)
            ? parsedKind
            : throw new InvalidDataException($"Unsupported manifest property kind '{kind}'.");

    private static WorkflowPropertyKind ParseSchemaPropertyKind(
        IReadOnlyList<string> typeNames,
        string? format)
    {
        if (typeNames.Any(typeName => string.Equals(typeName, "integer", StringComparison.OrdinalIgnoreCase)))
        {
            return WorkflowPropertyKind.Integer;
        }

        if (typeNames.Any(typeName => string.Equals(typeName, "number", StringComparison.OrdinalIgnoreCase)))
        {
            return WorkflowPropertyKind.Decimal;
        }

        if (typeNames.Any(typeName => string.Equals(typeName, "boolean", StringComparison.OrdinalIgnoreCase)))
        {
            return WorkflowPropertyKind.Boolean;
        }

        if (string.Equals(format, "uuid", StringComparison.OrdinalIgnoreCase))
        {
            return WorkflowPropertyKind.Guid;
        }

        if (string.Equals(format, "date", StringComparison.OrdinalIgnoreCase))
        {
            return WorkflowPropertyKind.Date;
        }

        if (string.Equals(format, "date-time", StringComparison.OrdinalIgnoreCase))
        {
            return WorkflowPropertyKind.DateTime;
        }

        return WorkflowPropertyKind.String;
    }

    private static bool IsDuplicateRegistrationRace(DbUpdateException exception)
    {
        var message = exception.ToString();
        return message.Contains("IX_TaskDefinitions_Name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase);
    }

    private string FindEntryPoint(string workspaceDirectory, string pluginName)
    {
        var appDir = Path.Combine(workspaceDirectory, "app");

        if (!Directory.Exists(appDir))
        {
            _logger.LogWarning("No app directory found at {AppDirectory}", appDir);
            throw new Exception();
        }

        string? entryPointPath = Directory.EnumerateFiles(appDir, "*.exe").FirstOrDefault() ??
            Directory.EnumerateFiles(appDir, pluginName).FirstOrDefault() ??
            Directory.EnumerateFiles(appDir, "*.dll").FirstOrDefault();

        if (entryPointPath is not null)
        {
            _logger.LogInformation("App entry: {Path}", entryPointPath);
            return Path.GetRelativePath(workspaceDirectory, entryPointPath);
        }        

        _logger.LogWarning("No app entry point found in {AppDirectory}", appDir);
        throw new Exception();
    }

    private sealed record SchemaPropertyInfo(
        WorkflowPropertyKind Kind,
        bool IsRequired,
        IReadOnlyList<string> EnumValues);
}
