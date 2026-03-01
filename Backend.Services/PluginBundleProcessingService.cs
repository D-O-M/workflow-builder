using System.Runtime.CompilerServices;
using Azure.Storage.Blobs;
using Backend.DTOs;
using Backend.DTOs.DomainModels;
using Backend.Interfaces.Repositories;
using Backend.Interfaces.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
namespace Backend.Services;

public sealed class PluginBundleProcessingService : IPluginBundleProcessingService
{
    private readonly BlobContainerClient _blobContainerClient;
    private readonly IPluginWorkspaceFactoryUtility _workspaceFactory;
    private readonly IPluginArchiveExtractionUtility _archiveExtractor;
    private readonly IPluginManifestParsingUtility _manifestParser;
    private readonly IPluginSchemaAggregationUtility _schemaAggregator;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PluginBundleProcessingService> _logger;

    public PluginBundleProcessingService(
        BlobContainerClient blobContainerClient,
        IPluginWorkspaceFactoryUtility workspaceFactory,
        IPluginArchiveExtractionUtility archiveExtractor,
        IPluginManifestParsingUtility manifestReader,
        IPluginSchemaAggregationUtility schemaAggregator,
        IServiceScopeFactory scopeFactory,
        ILogger<PluginBundleProcessingService> logger)
    {
        _blobContainerClient = blobContainerClient;
        _workspaceFactory = workspaceFactory;
        _archiveExtractor = archiveExtractor;
        _manifestParser = manifestReader;
        _schemaAggregator = schemaAggregator;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task ProcessAsync(PluginBlobInfo pluginInfo, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing plugin {Plugin} ({Version}) from blob {BlobName}", pluginInfo.PluginName, pluginInfo.Version, pluginInfo.BlobName);

        var blobClient = _blobContainerClient.GetBlobClient(pluginInfo.BlobName);
        var workspaceDirectory = await _workspaceFactory.CreateWorkspaceAsync(pluginInfo.PluginName, pluginInfo.Version, cancellationToken);

        await _archiveExtractor.ExtractAsync(blobClient, workspaceDirectory, cancellationToken);

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

        using var serviceScope = _scopeFactory.CreateScope();
        var workflowTaskRepository = serviceScope.ServiceProvider.GetRequiredService<IWorkflowTaskRepository>();

        var version = new TaskVersionDTO
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
            await workflowTaskRepository.CreateVersion(version, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to create workflow task version for plugin {Plugin} ({Version}).",
                pluginInfo.PluginName, pluginInfo.Version);
        }

        /*await workflowTaskRepository.CreateBasicTask(
            pluginInfo.PluginName,
            pluginInfo.Version.ToString(),
            entryPoint.ToString(),
            cancellationToken);*/
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

    private ChannelDefinitionDTO? GetInputChannelDefinition(
        PluginBlobInfo pluginInfo, ManifestFile manifest, PluginSchemaResult schemas)
    {
        var inputChannelType = manifest.InputChannel.Type;
        var inputChannelSchema = schemas.TypedSchemas!.Components.Schemas[inputChannelType];

        var propDefs = new HashSet<PropertyDefinitionDTO>();

        foreach (var property in inputChannelSchema.Properties)
        {
            var propDef = new PropertyDefinitionDTO
            {
                Name = property.Key,
                TypeName = property.Value.Type
            };

            if (propDefs.Contains(propDef))
            {
                _logger.LogWarning("Encountered more than one property named {PropertyName} while parsing " +
                    "the Input Channel definition for version {PluginVersion} of plugin {PluginName}.",
                    property.Key, pluginInfo.Version.ToString(), pluginInfo.PluginName);
            }
            else propDefs.Add(propDef);
        }

        return new ChannelDefinitionDTO
        {
            Name = pluginInfo.PluginName,
            ModelTypeName = inputChannelType,
            Properties = propDefs
        };
    }

    private HashSet<ChannelDefinitionDTO> GetOutputChannelDefinitions(
        PluginBlobInfo pluginInfo, ManifestFile manifest, PluginSchemaResult schemas)
    {
        var channelDefs = new HashSet<ChannelDefinitionDTO>();

        foreach (var channel in manifest.OutputChannels)
        {
            var channelType = channel.Type;
            var channelSchema = schemas.TypedSchemas!.Components.Schemas[channelType];

            var propDefs = new HashSet<PropertyDefinitionDTO>();

            foreach (var property in channelSchema.Properties)
            {
                var propDef = new PropertyDefinitionDTO
                {
                    Name = property.Key,
                    TypeName = property.Value.Type
                };

                if (!propDefs.Add(propDef))
                {
                    _logger.LogWarning("Encountered more than one identical property named {PropertyName} while parsing " +
                        "the Output Channel definition for channel {ChannelName} of version {PluginVersion} of plugin {PluginName}.",
                        channel.Name, property.Key, pluginInfo.Version.ToString(), pluginInfo.PluginName);
                }
            }

            if (!channelDefs.Add(new ChannelDefinitionDTO
            {
                Name = pluginInfo.PluginName,
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

    private HashSet<PropertyDefinitionDTO> GetGlobalStateRequirements(
        PluginBlobInfo pluginInfo, ManifestFile manifest, PluginSchemaResult schemas)
    {
        var globalStateType = manifest.GlobalState;
        var globalStateSchema = schemas.TypedSchemas!.Components.Schemas[globalStateType];

        var propDefs = new HashSet<PropertyDefinitionDTO>();

        foreach (var property in globalStateSchema.Properties)
        {
            var propDef = new PropertyDefinitionDTO
            {
                Name = property.Key,
                TypeName = property.Value.Type
            };

            if (!propDefs.Add(propDef))
            {
                _logger.LogWarning("Encountered more than one property named {PropertyName} while parsing " +
                    "the Global State definition for version {PluginVersion} of plugin {PluginName}.",
                    property.Key, pluginInfo.Version.ToString(), pluginInfo.PluginName);
            }
        }

        return propDefs;
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
