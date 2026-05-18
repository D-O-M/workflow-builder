namespace Backend.PluginAbi;

public enum PluginAbiCompatibilityStatus
{
    Compatible = 0,
    MissingMetadata = 1,
    Incompatible = 2
}

public sealed record PluginAbiCompatibilityIssue(string Code, string Message);

public sealed record PluginAbiCompatibilityResult(
    PluginAbiCompatibilityStatus Status,
    IReadOnlyList<PluginAbiCompatibilityIssue> Issues)
{
    public bool IsCompatible => Status == PluginAbiCompatibilityStatus.Compatible;
}

public sealed class PluginAbiCompatibilityException : Exception
{
    public PluginAbiCompatibilityException(string pluginName, Version pluginVersion, PluginAbiCompatibilityResult result)
        : base(BuildMessage(pluginName, pluginVersion, result))
    {
        PluginName = pluginName;
        PluginVersion = pluginVersion;
        Result = result;
    }

    public string PluginName { get; }
    public Version PluginVersion { get; }
    public PluginAbiCompatibilityResult Result { get; }

    private static string BuildMessage(string pluginName, Version pluginVersion, PluginAbiCompatibilityResult result)
    {
        var issues = result.Issues.Count == 0
            ? "No compatibility details were reported."
            : string.Join("; ", result.Issues.Select(issue => $"{issue.Code}: {issue.Message}"));

        return $"Plugin {pluginName} ({pluginVersion}) declares an incompatible plugin ABI. {issues}";
    }
}

public sealed class PluginAbiCompatibilityService
{
    private static readonly HashSet<string> SupportedStateStoreModes = new(
        PluginAbiDefaults.CurrentSupportedStateStoreModes,
        StringComparer.Ordinal);

    private static readonly HashSet<string> SupportedPropertyKinds = new(
        PluginAbiDefaults.CurrentSupportedPropertyKinds,
        StringComparer.Ordinal);

    public PluginAbiCompatibilityResult Evaluate(
        PluginAbiMetadata? metadata,
        IEnumerable<string>? declaredPropertyKinds = null)
    {
        if (metadata is null)
        {
            return new PluginAbiCompatibilityResult(
                PluginAbiCompatibilityStatus.MissingMetadata,
                [new("ABI_MISSING", "manifest.json must include an abi object.")]);
        }

        var issues = new List<PluginAbiCompatibilityIssue>();

        if (metadata.Version.Major != PluginAbiDefaults.CurrentMajor ||
            metadata.Version.Minor > PluginAbiDefaults.CurrentMinor ||
            metadata.Version.Minor < 0)
        {
            issues.Add(new(
                "ABI_VERSION_UNSUPPORTED",
                $"ABI version {metadata.Version} is not supported by Engine ABI {PluginAbiDefaults.CurrentVersion}."));
        }

        if (metadata.InvocationEnvelopeSchemaVersion != PluginAbiDefaults.CurrentInvocationEnvelopeSchemaVersion)
        {
            issues.Add(new(
                "INVOCATION_ENVELOPE_SCHEMA_UNSUPPORTED",
                $"Invocation envelope schema version {metadata.InvocationEnvelopeSchemaVersion} is not supported."));
        }

        if (metadata.ResultSchemaVersion != PluginAbiDefaults.CurrentResultSchemaVersion)
        {
            issues.Add(new(
                "RESULT_SCHEMA_UNSUPPORTED",
                $"Result schema version {metadata.ResultSchemaVersion} is not supported."));
        }

        if (metadata.HydrationProtocolVersion != PluginAbiDefaults.CurrentHydrationProtocolVersion)
        {
            issues.Add(new(
                "HYDRATION_PROTOCOL_UNSUPPORTED",
                $"Hydration protocol version {metadata.HydrationProtocolVersion} is not supported."));
        }

        if (string.IsNullOrWhiteSpace(metadata.HarnessVersion))
        {
            issues.Add(new("HARNESS_VERSION_MISSING", "Harness version must be declared."));
        }

        var stateStoreModes = metadata.SupportedStateStoreModes ?? [];
        if (stateStoreModes.Count == 0)
        {
            issues.Add(new("STATE_STORE_MODES_MISSING", "At least one supported state-store mode must be declared."));
        }

        foreach (var mode in stateStoreModes)
        {
            if (!SupportedStateStoreModes.Contains(mode))
            {
                issues.Add(new("STATE_STORE_MODE_UNSUPPORTED", $"State-store mode '{mode}' is not supported."));
            }
        }

        var abiPropertyKinds = metadata.SupportedPropertyKinds ?? [];
        if (abiPropertyKinds.Count == 0)
        {
            issues.Add(new("PROPERTY_KINDS_MISSING", "At least one supported property kind must be declared."));
        }

        var declaredKindSet = new HashSet<string>(abiPropertyKinds, StringComparer.Ordinal);
        foreach (var propertyKind in abiPropertyKinds)
        {
            if (!SupportedPropertyKinds.Contains(propertyKind))
            {
                issues.Add(new("PROPERTY_KIND_UNSUPPORTED", $"Property kind '{propertyKind}' is not supported."));
            }
        }

        foreach (var propertyKind in declaredPropertyKinds ?? [])
        {
            if (!declaredKindSet.Contains(propertyKind))
            {
                issues.Add(new(
                    "DECLARED_PROPERTY_KIND_NOT_COVERED",
                    $"Manifest declares property kind '{propertyKind}' but abi.supportedPropertyKinds does not include it."));
            }
        }

        return issues.Count == 0
            ? new PluginAbiCompatibilityResult(PluginAbiCompatibilityStatus.Compatible, [])
            : new PluginAbiCompatibilityResult(PluginAbiCompatibilityStatus.Incompatible, issues);
    }

    public void ThrowIfIncompatible(
        string pluginName,
        Version pluginVersion,
        PluginAbiMetadata? metadata,
        IEnumerable<string>? declaredPropertyKinds = null)
    {
        var result = Evaluate(metadata, declaredPropertyKinds);
        if (!result.IsCompatible)
        {
            throw new PluginAbiCompatibilityException(pluginName, pluginVersion, result);
        }
    }
}
