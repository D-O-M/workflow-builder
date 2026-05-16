namespace Backend.Engine.Capabilities;

public static class CapabilityKindNames
{
    public const string Software = "Software";
    public const string Network = "Network";
    public const string Configuration = "Configuration";
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class EngineCapabilityAttribute : Attribute
{
    public EngineCapabilityAttribute(string id, string kind, string displayName)
    {
        Id = id;
        Kind = kind;
        DisplayName = displayName;
    }

    public string Id { get; }
    public string Kind { get; }
    public string DisplayName { get; }
    public string? Description { get; init; }
    public bool Required { get; init; } = true;
    public bool Installable { get; init; }
    public string? Remediation { get; init; }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CommandCapabilityAttribute : Attribute
{
    public CommandCapabilityAttribute(string command)
    {
        Command = command;
    }

    public string Command { get; }
    public string Arguments { get; init; } = "--version";
    public string? MinimumVersion { get; init; }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class HttpEndpointCapabilityAttribute : Attribute
{
    public HttpEndpointCapabilityAttribute(string endpointConfigKey)
    {
        EndpointConfigKey = endpointConfigKey;
    }

    public string EndpointConfigKey { get; }
    public int TimeoutSeconds { get; init; } = 5;
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class RequiresConfigurationAttribute : Attribute
{
    public RequiresConfigurationAttribute(Type optionsType)
    {
        OptionsType = optionsType;
    }

    public Type OptionsType { get; }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class EngineConfigurationKeyAttribute : Attribute
{
    public EngineConfigurationKeyAttribute(string key)
    {
        Key = key;
    }

    public string Key { get; }
    public bool Required { get; init; } = true;
}
