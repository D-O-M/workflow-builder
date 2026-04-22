using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
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
            schemas,
            inputChannelType,
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
                schemas,
                channelType,
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

    private HashSet<PropertyDefinitionDomainModel> GetGlobalStateRequirements(
        PluginBlobInfo pluginInfo, ManifestFile manifest, PluginSchemaResult schemas)
    {
        var globalStateType = manifest.GlobalState;
        if (string.IsNullOrWhiteSpace(globalStateType))
        {
            _logger.LogInformation(
                "Plugin {Plugin} ({Version}) did not declare a global state schema.",
                pluginInfo.PluginName,
                pluginInfo.Version);
            return [];
        }

        return BuildPropertyDefinitions(
            pluginInfo,
            schemas,
            globalStateType,
            "global state");
    }

    private HashSet<PropertyDefinitionDomainModel> BuildPropertyDefinitions(
        PluginBlobInfo pluginInfo,
        PluginSchemaResult schemas,
        string schemaType,
        string schemaPurpose)
    {
        var propDefs = new HashSet<PropertyDefinitionDomainModel>();

        foreach (var property in GetSchemaProperties(pluginInfo, schemas, schemaType, schemaPurpose))
        {
            var propDef = new PropertyDefinitionDomainModel
            {
                Name = property.Key,
                TypeName = property.Value
            };

            if (!propDefs.Add(propDef))
            {
                _logger.LogWarning(
                    "Encountered more than one property named {PropertyName} while parsing the {SchemaPurpose} definition for plugin {PluginName} ({PluginVersion}).",
                    property.Key,
                    schemaPurpose,
                    pluginInfo.PluginName,
                    pluginInfo.Version);
            }
        }

        return propDefs;
    }

    private IReadOnlyDictionary<string, string> GetSchemaProperties(
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
                property => property.Value.Type,
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
        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static bool TryGetRawSchemaProperties(
        JsonNode aggregateNode,
        string schemaType,
        out IReadOnlyDictionary<string, string> properties)
    {
        properties = new Dictionary<string, string>(StringComparer.Ordinal);

        if (aggregateNode is not JsonObject root ||
            root["components"] is not JsonObject components ||
            components["schemas"] is not JsonObject schemas ||
            schemas[schemaType] is not JsonObject schemaObject)
        {
            return false;
        }

        if (schemaObject["properties"] is not JsonObject propertyNodes)
        {
            properties = new Dictionary<string, string>(StringComparer.Ordinal);
            return true;
        }

        var parsedProperties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var propertyNode in propertyNodes)
        {
            var propertyType = ResolvePropertyType(propertyNode.Value);
            if (string.IsNullOrWhiteSpace(propertyType))
            {
                continue;
            }

            parsedProperties[propertyNode.Key] = propertyType;
        }

        properties = parsedProperties;
        return true;
    }

    private static string? ResolvePropertyType(JsonNode? propertyNode)
    {
        if (propertyNode is not JsonObject propertyObject)
        {
            return null;
        }

        if (propertyObject["type"] is JsonValue stringValue &&
            stringValue.TryGetValue<string>(out var scalarType) &&
            !string.IsNullOrWhiteSpace(scalarType))
        {
            return scalarType;
        }

        if (propertyObject["type"] is JsonArray typeArray)
        {
            foreach (var candidateNode in typeArray)
            {
                if (candidateNode is not JsonValue candidateValue ||
                    !candidateValue.TryGetValue<string>(out var candidateType) ||
                    string.IsNullOrWhiteSpace(candidateType) ||
                    string.Equals(candidateType, "null", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return candidateType;
            }
        }

        return null;
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
            Directory.EnumerateFiles(appDir, "*.dll").FirstOrDefault() ??
            Directory.EnumerateFiles(appDir, pluginName).FirstOrDefault();

        if (entryPointPath is not null)
        {
            _logger.LogInformation("App entry: {Path}", entryPointPath);
            return Path.GetRelativePath(workspaceDirectory, entryPointPath);
        }        

        _logger.LogWarning("No app entry point found in {AppDirectory}", appDir);
        throw new Exception();
    }
}
