using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Database.Relational.Entities;
using Backend.Database.State.Objects;
using Backend.Interfaces.Queues;
using Backend.Models;
using Backend.Models.DomainModels;
using Backend.Interfaces.Repositories;
using Backend.Interfaces.Utilities;
using Backend.PluginAbi;
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
    private readonly IPluginIngestionStatusStore _pluginIngestionStatusStore;
    private readonly PluginAbiCompatibilityService _abiCompatibilityService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PluginBundleProcessingService> _logger;

    public PluginBundleProcessingService(
        IPluginBundleSource pluginBundleSource,
        IPluginWorkspaceFactoryUtility workspaceFactory,
        IPluginManifestParsingUtility manifestReader,
        IPluginSchemaAggregationUtility schemaAggregator,
        IPluginReloadQueueWriter pluginReloadQueueWriter,
        IPluginIngestionStatusStore pluginIngestionStatusStore,
        PluginAbiCompatibilityService abiCompatibilityService,
        IServiceScopeFactory scopeFactory,
        ILogger<PluginBundleProcessingService> logger)
    {
        _pluginBundleSource = pluginBundleSource;
        _workspaceFactory = workspaceFactory;
        _manifestParser = manifestReader;
        _schemaAggregator = schemaAggregator;
        _pluginReloadQueueWriter = pluginReloadQueueWriter;
        _pluginIngestionStatusStore = pluginIngestionStatusStore;
        _abiCompatibilityService = abiCompatibilityService;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task ProcessAsync(PluginBlobInfo pluginInfo, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing plugin {Plugin} ({Version}) from blob {BlobName}", pluginInfo.PluginName, pluginInfo.Version, pluginInfo.BlobName);

        if (await IsQuarantinedAsync(pluginInfo, cancellationToken))
        {
            _logger.LogWarning(
                "Skipping quarantined plugin bundle {BlobName} for plugin {Plugin} ({Version}).",
                pluginInfo.BlobName,
                pluginInfo.PluginName,
                pluginInfo.Version);
            return;
        }

        var pluginGate = _pluginProcessingGates.GetOrAdd(
            pluginInfo.PluginName,
            static _ => new SemaphoreSlim(1, 1));

        await pluginGate.WaitAsync(cancellationToken);
        try
        {
            await RecordStatusAsync(
                pluginInfo,
                PluginIngestionStatus.Extracting,
                "Plugin bundle extraction started.",
                cancellationToken);

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
                await RecordStatusAsync(
                    pluginInfo,
                    PluginIngestionStatus.DuplicateIgnored,
                    "Plugin version is already registered.",
                    cancellationToken);
                return;
            }

            var workspaceDirectory = await _workspaceFactory.CreateWorkspaceAsync(pluginInfo.PluginName, pluginInfo.Version, cancellationToken);

            await _pluginBundleSource.ExtractBundleAsync(pluginInfo, workspaceDirectory, cancellationToken);

            ManifestFile? manifest;
            try
            {
                manifest = await _manifestParser.ReadAsync(workspaceDirectory, cancellationToken);
            }
            catch (JsonException ex)
            {
                await RecordInvalidAndQuarantineAsync(
                    pluginInfo,
                    PluginIngestionStatus.ManifestInvalid,
                    ex.Message,
                    cancellationToken);
                throw;
            }

            if (manifest is null)
            {
                _logger.LogWarning("Manifest not found for plugin {Plugin} ({Version}). Processing aborted.", pluginInfo.PluginName, pluginInfo.Version);
                await RecordInvalidAndQuarantineAsync(
                    pluginInfo,
                    PluginIngestionStatus.ManifestMissing,
                    "manifest.json was not found in the plugin bundle.",
                    cancellationToken);
                return;
            }

            try
            {
                ValidateAbi(pluginInfo, manifest);
            }
            catch (PluginAbiCompatibilityException ex)
            {
                await RecordInvalidAndQuarantineAsync(
                    pluginInfo,
                    PluginIngestionStatus.AbiInvalid,
                    ex.Message,
                    cancellationToken);
                throw;
            }

            try
            {
                ValidateCapabilityRequirements(pluginInfo, manifest);
            }
            catch (InvalidDataException ex)
            {
                await RecordInvalidAndQuarantineAsync(
                    pluginInfo,
                    PluginIngestionStatus.CapabilityMetadataInvalid,
                    ex.Message,
                    cancellationToken);
                throw;
            }

            try
            {
                ValidateConfigurationRequirements(pluginInfo, manifest);
            }
            catch (InvalidDataException ex)
            {
                await RecordInvalidAndQuarantineAsync(
                    pluginInfo,
                    PluginIngestionStatus.ConfigurationMetadataInvalid,
                    ex.Message,
                    cancellationToken);
                throw;
            }

            TaskVersionDomainModel version;
            try
            {
                var loadedSchemas = await _schemaAggregator.LoadAsync(workspaceDirectory, cancellationToken);
                LogProcessingSummary(pluginInfo, manifest, loadedSchemas);
                var schemas = loadedSchemas ?? throw new InvalidDataException("Plugin schema metadata is missing.");

                var entryPoint = FindEntryPoint(workspaceDirectory, pluginInfo.PluginName);

                version = new TaskVersionDomainModel
                {
                    Name = pluginInfo.PluginName,
                    DisplayName = manifest.DisplayName,
                    Version = pluginInfo.Version,
                    WorkspaceEntryPointPath = entryPoint,
                    Abi = manifest.Abi!,
                    InputChannelDefinition = GetInputChannelDefinition(pluginInfo, manifest, schemas),
                    OutputChannelDefinitions = GetOutputChannelDefinitions(pluginInfo, manifest, schemas)
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                await RecordInvalidAndQuarantineAsync(
                    pluginInfo,
                    PluginIngestionStatus.SchemaInvalid,
                    ex.Message,
                    cancellationToken);
                throw;
            }

            try
            {
                var registrationResult = await taskDefinitionRepository.CreateVersion(version, cancellationToken);
                await RecordStatusAsync(
                    pluginInfo,
                    PluginIngestionStatus.Registered,
                    "Plugin version registered.",
                    cancellationToken,
                    registrationResult.TaskVersionId);
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
                await RecordStatusAsync(
                    pluginInfo,
                    PluginIngestionStatus.DuplicateIgnored,
                    "Plugin version was registered by another detector invocation.",
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to create task definition version for plugin {Plugin} ({Version}).",
                    pluginInfo.PluginName, pluginInfo.Version);
                throw;
            }
        }
        finally
        {
            pluginGate.Release();
        }
    }

    private async Task<bool> IsQuarantinedAsync(
        PluginBlobInfo pluginInfo,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _pluginIngestionStatusStore.IsQuarantinedAsync(pluginInfo.BlobName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Unable to read ingestion quarantine status for plugin {Plugin} ({Version}). Continuing processing.",
                pluginInfo.PluginName,
                pluginInfo.Version);
            return false;
        }
    }

    private async Task RecordInvalidAndQuarantineAsync(
        PluginBlobInfo pluginInfo,
        PluginIngestionStatus status,
        string? message,
        CancellationToken cancellationToken)
    {
        await RecordStatusAsync(
            pluginInfo,
            status,
            message,
            cancellationToken);
        await RecordStatusAsync(
            pluginInfo,
            PluginIngestionStatus.Quarantined,
            message,
            cancellationToken);
    }

    private async Task RecordStatusAsync(
        PluginBlobInfo pluginInfo,
        PluginIngestionStatus status,
        string? message,
        CancellationToken cancellationToken,
        int? taskVersionId = null)
    {
        try
        {
            await _pluginIngestionStatusStore.AppendEventAsync(
                PluginIngestionStatusUpdate.FromPlugin(
                    pluginInfo.BlobName,
                    pluginInfo.PluginName,
                    pluginInfo.Version,
                    status,
                    message,
                    taskVersionId),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Unable to record plugin ingestion status {Status} for plugin {Plugin} ({Version}).",
                status,
                pluginInfo.PluginName,
                pluginInfo.Version);
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

    private void ValidateAbi(PluginBlobInfo pluginInfo, ManifestFile manifest)
    {
        var declaredPropertyKinds = GetDeclaredAbiPropertyKinds(manifest);
        var result = _abiCompatibilityService.Evaluate(manifest.Abi, declaredPropertyKinds);
        if (result.IsCompatible)
        {
            return;
        }

        _logger.LogWarning(
            "Plugin {Plugin} ({Version}) declares incompatible ABI metadata: {Issues}",
            pluginInfo.PluginName,
            pluginInfo.Version,
            string.Join("; ", result.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));

        throw new PluginAbiCompatibilityException(pluginInfo.PluginName, pluginInfo.Version, result);
    }

    private static IReadOnlyCollection<string> GetDeclaredAbiPropertyKinds(ManifestFile manifest)
    {
        var propertyKinds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var channel in EnumerateChannels(manifest))
        {
            foreach (var property in channel.Properties)
            {
                propertyKinds.Add(NormalizePropertyKind(property.Kind));
            }
        }

        foreach (var requirement in manifest.Configuration.Requirements)
        {
            if (!string.IsNullOrWhiteSpace(requirement.Kind))
            {
                propertyKinds.Add(NormalizePropertyKind(requirement.Kind));
            }
        }

        return propertyKinds;
    }

    private static IEnumerable<ManifestFileChannelInfo> EnumerateChannels(ManifestFile manifest)
    {
        if (manifest.InputChannel is not null)
        {
            yield return manifest.InputChannel;
        }

        foreach (var outputChannel in manifest.OutputChannels)
        {
            yield return outputChannel;
        }
    }

    private static string NormalizePropertyKind(string kind)
    {
        if (WorkflowPropertyTypeName.TryParse(kind, out var parsedKind, out _))
        {
            return parsedKind.ToString();
        }

        if (string.Equals(kind, "Uri", StringComparison.Ordinal) ||
            string.Equals(kind, "TimeSpan", StringComparison.Ordinal))
        {
            return WorkflowPropertyKind.String.ToString();
        }

        return kind.Trim();
    }

    private static void ValidateConfigurationRequirements(PluginBlobInfo pluginInfo, ManifestFile manifest)
    {
        var byKey = new Dictionary<string, ManifestFileConfigurationRequirementInfo>(StringComparer.Ordinal);
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var capabilitiesById = manifest.Capabilities.Requirements
            .GroupBy(requirement => requirement.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var capabilityConfigurationKeys = manifest.Capabilities.Requirements
            .SelectMany(requirement => requirement.ConfigurationKeys.Select(key => (requirement.Id, Key: key)))
            .ToHashSet();

        foreach (var requirement in manifest.Configuration.Requirements)
        {
            if (string.IsNullOrWhiteSpace(requirement.DomainId) ||
                string.IsNullOrWhiteSpace(requirement.OptionsName) ||
                string.IsNullOrWhiteSpace(requirement.PropertyPath) ||
                string.IsNullOrWhiteSpace(requirement.Key) ||
                string.IsNullOrWhiteSpace(requirement.OptionsType))
            {
                throw new InvalidDataException(
                    $"Plugin {pluginInfo.PluginName} ({pluginInfo.Version}) has an incomplete configuration requirement.");
            }

            if (string.IsNullOrWhiteSpace(requirement.SourceCapabilityId) ||
                !capabilitiesById.TryGetValue(requirement.SourceCapabilityId, out var sourceCapability))
            {
                throw new InvalidDataException(
                    $"Plugin {pluginInfo.PluginName} ({pluginInfo.Version}) configuration key '{requirement.Key}' must be owned by a declared capability.");
            }

            if (string.IsNullOrWhiteSpace(requirement.SourceCapabilityType) ||
                !string.Equals(requirement.SourceCapabilityType, sourceCapability.CapabilityType, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Plugin {pluginInfo.PluginName} ({pluginInfo.Version}) configuration key '{requirement.Key}' declares source capability type '{requirement.SourceCapabilityType}', but capability '{requirement.SourceCapabilityId}' uses '{sourceCapability.CapabilityType}'.");
            }

            if (!capabilityConfigurationKeys.Contains((requirement.SourceCapabilityId, requirement.Key)))
            {
                throw new InvalidDataException(
                    $"Plugin {pluginInfo.PluginName} ({pluginInfo.Version}) capability '{requirement.SourceCapabilityId}' does not list owned configuration key '{requirement.Key}'.");
            }

            var expectedPrefix = $"{requirement.DomainId}:{requirement.OptionsName}:";
            if (!requirement.Key.StartsWith(expectedPrefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Plugin {pluginInfo.PluginName} ({pluginInfo.Version}) configuration key '{requirement.Key}' must start with '{expectedPrefix}'.");
            }

            var expectedOptionsTypePrefix = $"Backend.Plugins.Configuration.{requirement.DomainId}.";
            if (!requirement.OptionsType.Replace("global::", string.Empty).StartsWith(expectedOptionsTypePrefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Plugin {pluginInfo.PluginName} ({pluginInfo.Version}) configuration type '{requirement.OptionsType}' is outside the approved domain '{expectedOptionsTypePrefix}'.");
            }

            if (byKey.TryGetValue(requirement.Key, out var existing))
            {
                if (!string.Equals(existing.ClrType, requirement.ClrType, StringComparison.Ordinal) ||
                    !string.Equals(existing.Kind, requirement.Kind, StringComparison.Ordinal) ||
                    !string.Equals(existing.OptionsType, requirement.OptionsType, StringComparison.Ordinal) ||
                    !string.Equals(existing.OptionsName, requirement.OptionsName, StringComparison.Ordinal) ||
                    !string.Equals(existing.PropertyPath, requirement.PropertyPath, StringComparison.Ordinal) ||
                    !string.Equals(existing.Description ?? "", requirement.Description ?? "", StringComparison.Ordinal) ||
                    !string.Equals(existing.SourceCapabilityId, requirement.SourceCapabilityId, StringComparison.Ordinal) ||
                    !string.Equals(existing.SourceCapabilityType, requirement.SourceCapabilityType, StringComparison.Ordinal) ||
                    existing.Secret != requirement.Secret ||
                    existing.Required != requirement.Required)
                {
                    throw new InvalidDataException(
                        $"Plugin {pluginInfo.PluginName} ({pluginInfo.Version}) declares conflicting metadata for configuration key '{requirement.Key}'.");
                }
            }
            else
            {
                byKey[requirement.Key] = requirement;
            }

            var alias = requirement.PropertyPath.Split(':', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (string.IsNullOrWhiteSpace(alias))
            {
                continue;
            }

            if (aliases.TryGetValue(alias, out var existingKey) &&
                !string.Equals(existingKey, requirement.Key, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Plugin {pluginInfo.PluginName} ({pluginInfo.Version}) declares short configuration alias '{alias}' for both '{existingKey}' and '{requirement.Key}'.");
            }

            aliases[alias] = requirement.Key;
        }
    }

    private static void ValidateCapabilityRequirements(PluginBlobInfo pluginInfo, ManifestFile manifest)
    {
        var byId = new Dictionary<string, ManifestFileCapabilityRequirementInfo>(StringComparer.Ordinal);
        foreach (var requirement in manifest.Capabilities.Requirements)
        {
            if (string.IsNullOrWhiteSpace(requirement.Id) ||
                string.IsNullOrWhiteSpace(requirement.Kind) ||
                string.IsNullOrWhiteSpace(requirement.CapabilityType) ||
                string.IsNullOrWhiteSpace(requirement.DisplayName))
            {
                throw new InvalidDataException(
                    $"Plugin {pluginInfo.PluginName} ({pluginInfo.Version}) has an incomplete capability requirement.");
            }

            var normalizedCapabilityType = requirement.CapabilityType.Replace("global::", string.Empty);
            if (!normalizedCapabilityType.StartsWith("Backend.Engine.Capabilities.", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Plugin {pluginInfo.PluginName} ({pluginInfo.Version}) capability type '{requirement.CapabilityType}' is outside the approved Backend.Engine.Capabilities namespace.");
            }

            if (byId.TryGetValue(requirement.Id, out var existing))
            {
                if (!string.Equals(existing.Kind, requirement.Kind, StringComparison.Ordinal) ||
                    !string.Equals(existing.CapabilityType, requirement.CapabilityType, StringComparison.Ordinal) ||
                    !string.Equals(existing.DisplayName, requirement.DisplayName, StringComparison.Ordinal) ||
                    !string.Equals(existing.Description ?? "", requirement.Description ?? "", StringComparison.Ordinal) ||
                    !string.Equals(existing.Remediation ?? "", requirement.Remediation ?? "", StringComparison.Ordinal) ||
                    !string.Equals(existing.Command ?? "", requirement.Command ?? "", StringComparison.Ordinal) ||
                    !string.Equals(existing.Arguments ?? "", requirement.Arguments ?? "", StringComparison.Ordinal) ||
                    !string.Equals(existing.MinimumVersion ?? "", requirement.MinimumVersion ?? "", StringComparison.Ordinal) ||
                    !string.Equals(existing.EndpointConfigKey ?? "", requirement.EndpointConfigKey ?? "", StringComparison.Ordinal) ||
                    existing.TimeoutSeconds != requirement.TimeoutSeconds ||
                    !StringListEquals(existing.ConfigurationKeys, requirement.ConfigurationKeys) ||
                    !StringListEquals(existing.EngineConfigurationKeys, requirement.EngineConfigurationKeys) ||
                    existing.Required != requirement.Required ||
                    existing.Installable != requirement.Installable)
                {
                    throw new InvalidDataException(
                        $"Plugin {pluginInfo.PluginName} ({pluginInfo.Version}) declares conflicting metadata for capability '{requirement.Id}'.");
                }
            }
            else
            {
                byId[requirement.Id] = requirement;
            }
        }
    }

    private static bool StringListEquals(
        IReadOnlyCollection<string> left,
        IReadOnlyCollection<string> right) =>
        left.OrderBy(value => value, StringComparer.Ordinal)
            .SequenceEqual(
                right.OrderBy(value => value, StringComparer.Ordinal),
                StringComparer.Ordinal);

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
                    Kind = ParseManifestPropertyKind(property.Kind, out var isCollection),
                    IsCollection = isCollection,
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
                IsCollection = property.Value.IsCollection,
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
                    ParseSchemaPropertyKind(property.Value, out var isCollection),
                    property.Value.Type.All(typeName => !string.Equals(typeName, "null", StringComparison.OrdinalIgnoreCase)),
                    isCollection ? property.Value.Items?.Enum ?? [] : property.Value.Enum ?? [],
                    isCollection),
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

        if (typeNames.Any(typeName => string.Equals(typeName, "array", StringComparison.OrdinalIgnoreCase)))
        {
            if (propertyObject["items"] is not JsonObject itemsObject)
            {
                throw new InvalidDataException("Collection properties must declare a JSON Schema items type.");
            }

            var itemTypeNames = ReadTypeNames(itemsObject["type"]);
            if (itemTypeNames.Any(typeName => string.Equals(typeName, "array", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("Nested collection properties are not supported.");
            }

            var itemEnumValues = itemsObject["enum"] is JsonArray itemEnumArray
                ? itemEnumArray
                    .Select(value => value?.GetValue<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Cast<string>()
                    .ToArray()
                : [];

            return new SchemaPropertyInfo(
                ParseSchemaPropertyKind(itemTypeNames, itemsObject["format"]?.GetValue<string>()),
                typeNames.All(typeName => !string.Equals(typeName, "null", StringComparison.OrdinalIgnoreCase)),
                itemEnumValues,
                IsCollection: true);
        }

        return new SchemaPropertyInfo(
            ParseSchemaPropertyKind(typeNames, format),
            typeNames.All(typeName => !string.Equals(typeName, "null", StringComparison.OrdinalIgnoreCase)),
            enumValues,
            IsCollection: false);
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

    private static WorkflowPropertyKind ParseManifestPropertyKind(string kind, out bool isCollection) =>
        WorkflowPropertyTypeName.TryParse(kind, out var parsedKind, out isCollection)
            ? parsedKind
            : throw new InvalidDataException($"Unsupported manifest property kind '{kind}'.");

    private static WorkflowPropertyKind ParseSchemaPropertyKind(TypeInfo property, out bool isCollection)
    {
        var typeNames = property.Type;
        if (!typeNames.Any(typeName => string.Equals(typeName, "array", StringComparison.OrdinalIgnoreCase)))
        {
            isCollection = false;
            return ParseSchemaPropertyKind(typeNames, property.Format);
        }

        if (property.Items is null)
        {
            throw new InvalidDataException("Collection properties must declare a JSON Schema items type.");
        }

        if (property.Items.Type.Any(typeName => string.Equals(typeName, "array", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Nested collection properties are not supported.");
        }

        isCollection = true;
        return ParseSchemaPropertyKind(property.Items.Type, property.Items.Format);
    }

    private static WorkflowPropertyKind ParseSchemaPropertyKind(
        IReadOnlyList<string> typeNames,
        string? format)
    {
        if (typeNames.Any(typeName => string.Equals(typeName, "array", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Nested collection properties are not supported.");
        }

        if (typeNames.Any(typeName => string.Equals(typeName, "object", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Object properties are not supported.");
        }

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
        IReadOnlyList<string> EnumValues,
        bool IsCollection);
}
