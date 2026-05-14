namespace Backend.Plugins.Configuration;

public static class PluginConfigurationKey
{
    public const string DomainNamespacePrefix = "Backend.Plugins.Configuration.";

    public static bool IsDomainOptionsType(Type optionsType) =>
        optionsType.Namespace?.StartsWith(DomainNamespacePrefix, StringComparison.Ordinal) == true &&
        !optionsType.Namespace.Equals(
            "Backend.Plugins.Configuration.Abstractions",
            StringComparison.Ordinal);

    public static string GetDomainId(Type optionsType)
    {
        var relativeNamespace = GetRelativeNamespace(optionsType);
        var separatorIndex = relativeNamespace.IndexOf('.');
        return separatorIndex < 0
            ? relativeNamespace
            : relativeNamespace[..separatorIndex];
    }

    public static string GetOptionsSectionName(Type optionsType)
    {
        const string suffix = "Options";
        return optionsType.Name.EndsWith(suffix, StringComparison.Ordinal) &&
               optionsType.Name.Length > suffix.Length
            ? optionsType.Name[..^suffix.Length]
            : optionsType.Name;
    }

    public static string GetSectionPath(Type optionsType) =>
        $"{GetDomainId(optionsType)}:{GetOptionsSectionName(optionsType)}";

    private static string GetRelativeNamespace(Type optionsType)
    {
        var namespaceName = optionsType.Namespace;
        if (namespaceName is null ||
            !namespaceName.StartsWith(DomainNamespacePrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Plugin configuration type '{optionsType.FullName}' must live under '{DomainNamespacePrefix}'.");
        }

        var relativeNamespace = namespaceName[DomainNamespacePrefix.Length..];
        if (string.IsNullOrWhiteSpace(relativeNamespace))
        {
            throw new InvalidOperationException(
                $"Plugin configuration type '{optionsType.FullName}' must declare a domain namespace.");
        }

        return relativeNamespace;
    }
}
