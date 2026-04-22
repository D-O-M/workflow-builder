using System.Reflection;
using System.Runtime.Loader;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: Backend.Plugins.Harness.ContractEmitter <plugin-assembly-path> <output-directory>");
    return 1;
}

var pluginAssemblyPath = Path.GetFullPath(args[0]);
var outputDirectory = Path.GetFullPath(args[1]);

if (!File.Exists(pluginAssemblyPath))
{
    Console.Error.WriteLine($"Plugin assembly not found: {pluginAssemblyPath}");
    return 1;
}

Directory.CreateDirectory(outputDirectory);

var loadContext = new AssemblyLoadContext("WorkflowBuilderPluginContractEmitter", isCollectible: true);
var resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
loadContext.Resolving += (_, assemblyName) =>
{
    var resolvedPath = resolver.ResolveAssemblyToPath(assemblyName);
    return resolvedPath is null ? null : loadContext.LoadFromAssemblyPath(resolvedPath);
};

try
{
    var pluginAssembly = loadContext.LoadFromAssemblyPath(pluginAssemblyPath);
    var providerTypeName = pluginAssembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => string.Equals(attribute.Key, "WorkflowBuilder.PluginContractProvider", StringComparison.Ordinal))
        ?.Value;

    if (string.IsNullOrWhiteSpace(providerTypeName))
    {
        Console.Error.WriteLine($"No generated plugin contract provider metadata was found on assembly '{pluginAssembly.GetName().Name}'.");
        return 1;
    }

    var providerType = pluginAssembly.GetType(providerTypeName, throwOnError: false, ignoreCase: false);
    if (providerType is null)
    {
        Console.Error.WriteLine($"Generated contract provider type '{providerTypeName}' could not be loaded from assembly '{pluginAssembly.FullName}'.");
        return 1;
    }

    var manifestJson = ReadRequiredProperty(providerType, "ManifestJson");
    var schemasJson = ReadRequiredProperty(providerType, "SchemasJson");

    await File.WriteAllTextAsync(Path.Combine(outputDirectory, "manifest.json"), manifestJson);
    await File.WriteAllTextAsync(Path.Combine(outputDirectory, "schemas.json"), schemasJson);
    return 0;
}
finally
{
    loadContext.Unload();
}

static string ReadRequiredProperty(Type providerType, string propertyName)
{
    var property = providerType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
    if (property?.PropertyType != typeof(string))
    {
        throw new InvalidOperationException(
            $"Generated contract provider '{providerType.FullName}' is missing required static string property '{propertyName}'.");
    }

    return property.GetValue(null) as string
        ?? throw new InvalidOperationException(
            $"Generated contract provider '{providerType.FullName}' returned null for property '{propertyName}'.");
}
