using Backend.Interfaces.Utilities;
using Backend.Engine.Capabilities;
using Backend.Models;
using Backend.Services.EngineManagement;

namespace Backend.Engine;

// The validator and child process consume the same immutable, per-launch configuration.
public sealed class PluginLaunchConfiguration
{
    private readonly Dictionary<string, ManagedSetting?> _bindings;
    private readonly string[] _secrets;
    private readonly HashSet<string> _managedKeys;
    public ManifestFile? Manifest { get; }
    public IConfiguration Values { get; }
    public IReadOnlyList<CapabilityRequirementDescriptor> CapabilityChecks { get; }
    public IReadOnlyList<ConfigurationRequirementDescriptor> ConfigurationChecks { get; }
    public bool HasManagedOverrides => _bindings.Values.Any(binding => binding is not null);

    private PluginLaunchConfiguration(ManifestFile? manifest, IConfiguration values,
        Dictionary<string, ManagedSetting?> bindings, string[] secrets, HashSet<string> managedKeys,
        IReadOnlyList<CapabilityRequirementDescriptor> checks, IReadOnlyList<ConfigurationRequirementDescriptor> configurationChecks)
    {
        Manifest = manifest;
        Values = values;
        _bindings = bindings;
        _secrets = secrets;
        _managedKeys = managedKeys;
        CapabilityChecks = checks;
        ConfigurationChecks = configurationChecks;
    }

    public bool NeedsReplacement(EnginePolicySnapshot policy) =>
        _bindings.Any(binding => binding.Value != policy.Settings.GetValueOrDefault(binding.Key));

    public string Redact(string text)
    {
        foreach (var value in _secrets)
        {
            text = text.Replace(value, "[redacted]", StringComparison.Ordinal);
        }
        return text;
    }

    // CliWrap inherits the Engine environment. Explicitly remove the deployment
    // copies of managed keys: the typed per-launch files are the child's input.
    public bool IsPrivateEngineVariable(string name)
    {
        var canonical = name.Replace("__", ":", StringComparison.Ordinal);
        return _managedKeys.Contains(canonical) || canonical.StartsWith("EngineManagement:", StringComparison.OrdinalIgnoreCase) ||
            name.ToUpperInvariant() is "IDENTITY_ENDPOINT" or "IDENTITY_HEADER" or "MSI_ENDPOINT" or "MSI_SECRET"
                or "AZURE_CLIENT_SECRET" or "AZURE_CLIENT_CERTIFICATE_PASSWORD" or "AZURE_CLIENT_CERTIFICATE_PATH"
                or "AZURE_FEDERATED_TOKEN_FILE";
    }

    public static async Task<PluginLaunchConfiguration> ResolveAsync(
        ManifestFile? manifest, IConfiguration deployment, EnginePolicySnapshot? policy,
        Func<IManagementSecretStore>? getSecretStore, CancellationToken cancellationToken,
        ManagementSettingsCatalog? catalog = null)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var secretKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var descriptors = manifest?.Capabilities.Requirements.Select(PluginRuntimeCapabilityService.MapCapability).ToArray() ?? [];
        var checks = catalog is null ? descriptors : TrustedCapabilityChecks.Resolve(descriptors);
        var configurationChecks = catalog is null ? [] : TrustedCapabilityChecks.ConfigurationFor(checks, catalog);
        var pluginFields = configurationChecks.ToDictionary(field => field.Key, StringComparer.Ordinal);
        foreach (var requirement in manifest?.Configuration.Requirements ?? [])
        {
            if (catalog is not null && (!pluginFields.TryGetValue(requirement.Key, out var field) || field.Secret != requirement.Secret))
            {
                throw new InvalidDataException("The Plugin requests a setting outside its trusted capability contract.");
            }
            keys.Add(requirement.Key);
            if (requirement.Secret)
            {
                secretKeys.Add(requirement.Key);
            }
        }
        foreach (var capability in checks)
        {
            keys.UnionWith(capability.EngineConfigurationKeys);
            if (capability.EndpointConfigKey is { Length: > 0 } endpointKey)
            {
                keys.Add(endpointKey);
            }
        }

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var bindings = new Dictionary<string, ManagedSetting?>(StringComparer.OrdinalIgnoreCase);
        var capabilityIds = manifest?.Capabilities.Requirements.Select(requirement => requirement.Id).ToHashSet()
            ?? [];
        var eligibleKeys = catalog?.Fields.Where(field => capabilityIds.Contains(field.CapabilityId))
            .Select(field => field.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var managed = eligibleKeys?.Contains(key) == true;
            var binding = managed ? policy?.Settings.GetValueOrDefault(key) : null;
            if (managed) bindings.Add(key, binding);
            if (binding?.SecretReference is { } reference)
            {
                try
                {
                    values[key] = await getSecretStore!().ReadAsync(reference, cancellationToken);
                    secretKeys.Add(key);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // Provider errors may include credentials or vault details.
                    throw new PluginSecretUnavailableException();
                }
            }
            else if (binding is not null)
            {
                values[key] = binding.Value;
            }
            else
            {
                var section = deployment.GetSection(key);
                foreach (var pair in section.AsEnumerable())
                {
                    values[pair.Key] = pair.Value;
                }
            }
        }
        var secrets = secretKeys.Select(k => values.GetValueOrDefault(k))
            .OfType<string>().Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length).ToArray();
        var managedKeys = catalog?.Fields.Select(field => field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new(manifest, new ConfigurationBuilder().AddInMemoryCollection(values).Build(), bindings,
            secrets, managedKeys, checks, configurationChecks);
    }

    public static async Task<ManifestFile?> ReadManifestAsync(
        FileInfo entryPoint, IPluginManifestParsingUtility parser, CancellationToken cancellationToken)
    {
        for (var directory = entryPoint.Directory; directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "manifest.json")))
            {
                return await parser.ReadAsync(directory.FullName, cancellationToken);
            }
        }
        throw new FileNotFoundException($"Could not find manifest.json for Plugin entry point '{entryPoint.Name}'.");
    }
}

internal sealed class PluginSecretUnavailableException() : Exception("The Plugin credential could not be loaded.");
