using System.Diagnostics;
using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.Hosting;

public static class LocalDevelopmentResourceSettings
{
    private const string LocalCosmosConnectionString =
        "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;";
    private const string LocalServiceBusConnectionString =
        "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";
    private const string LocalBlobConnectionString = "UseDevelopmentStorage=true";
    private const string DefaultWorkflowTaskQueueName = "workflowtaskqueue";
    private const string DefaultPluginReloadQueueName = "pluginreloadqueue";
    private const string DefaultPluginsBlobContainerName = "pluginblob";

    public static string GetTasksDbConnectionString(
        IConfiguration configuration,
        IHostEnvironment environment) =>
        TryGetConnectionString(configuration, "TasksDB")
        ?? TryGetConnectionString(configuration, "SqlServer")
        ?? ThrowMissingConnectionString(
            "SQL Server",
            "TasksDB or SqlServer",
            environment);

    public static string GetCosmosConnectionString(
        IConfiguration configuration,
        IHostEnvironment environment) =>
        NormalizeLocalCosmosConnectionString(
            TryGetConfigurationValue(configuration, "AzureCosmos:ConnectionString")
            ?? TryGetConfiguredConnectionString(configuration, "AzureCosmos:ConnectionName")
            ?? TryGetConnectionString(configuration, "enginedb"),
            configuration,
            environment)
        ?? TryGetAzuriteMappedCosmosConnectionString(configuration, environment)
        ?? UseDevelopmentFallback(environment, LocalCosmosConnectionString)
        ?? ThrowMissingConnectionString(
            "Azure Cosmos",
            "AzureCosmos:ConnectionName or enginedb",
            environment);

    public static string GetWorkflowTaskQueueConnectionString(
        IConfiguration configuration,
        IHostEnvironment environment) =>
        TryGetConfiguredConnectionString(configuration, "AzureServiceBus:Queues:WorkflowTaskQueue:ConnectionName")
        ?? TryGetConnectionString(configuration, "workflow-task-queue")
        ?? TryGetConnectionString(configuration, "ServiceBus")
        ?? UseDevelopmentFallback(environment, LocalServiceBusConnectionString)
        ?? ThrowMissingConnectionString(
            "workflow task queue",
            "AzureServiceBus:Queues:WorkflowTaskQueue:ConnectionName, workflow-task-queue, or ServiceBus",
            environment);

    public static string GetWorkflowTaskQueueName(
        IConfiguration configuration,
        IHostEnvironment environment) =>
        TryGetServiceBusEntityPath(GetWorkflowTaskQueueConnectionString(configuration, environment))
        ?? TryGetConfigurationValue(configuration, "AzureServiceBus:Queues:WorkflowTaskQueue:Name")
        ?? TryGetConfigurationValue(configuration, "AzureServiceBus:Queues:WorkflowTaskQueue")
        ?? UseDevelopmentFallback(environment, DefaultWorkflowTaskQueueName)
        ?? throw new InvalidOperationException("Workflow task queue name is not configured.");

    public static string GetPluginReloadQueueConnectionString(
        IConfiguration configuration,
        IHostEnvironment environment) =>
        TryGetConfiguredConnectionString(configuration, "AzureServiceBus:Queues:PluginControlQueue:ConnectionName")
        ?? TryGetConnectionString(configuration, "plugin-control-queue")
        ?? TryGetConnectionString(configuration, "ServiceBus")
        ?? UseDevelopmentFallback(environment, LocalServiceBusConnectionString)
        ?? ThrowMissingConnectionString(
            "plugin reload queue",
            "AzureServiceBus:Queues:PluginControlQueue:ConnectionName, plugin-control-queue, or ServiceBus",
            environment);

    public static string GetPluginReloadQueueName(
        IConfiguration configuration,
        IHostEnvironment environment) =>
        TryGetServiceBusEntityPath(GetPluginReloadQueueConnectionString(configuration, environment))
        ?? TryGetConfigurationValue(configuration, "AzureServiceBus:Queues:PluginControlQueue:Name")
        ?? TryGetConfigurationValue(configuration, "AzureServiceBus:Queues:PluginControlQueue")
        ?? UseDevelopmentFallback(environment, DefaultPluginReloadQueueName)
        ?? throw new InvalidOperationException("Plugin reload queue name is not configured.");

    public static string GetBlobConnectionString(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var configuredConnectionString =
            TryGetConnectionString(configuration, "BlobConnection")
            ?? TryGetStorageResourceConnectionString(configuration, "BlobConnection");

        return NormalizeLocalBlobConnectionString(configuredConnectionString, environment)
            ?? TryGetAzuriteMappedBlobConnectionString(environment)
            ?? UseDevelopmentFallback(environment, LocalBlobConnectionString)
            ?? ThrowMissingConnectionString(
            "blob storage",
            "BlobConnection",
            environment);
    }

    public static string GetPluginsBlobContainerName(
        IConfiguration configuration,
        IHostEnvironment environment) =>
        TryGetConfigurationValue(configuration, "AzureStorage:BlobContainers:Plugins:ContainerName")
        ?? UseDevelopmentFallback(environment, DefaultPluginsBlobContainerName)
        ?? throw new InvalidOperationException("Plugins blob container name is not configured.");

    private static string? TryGetConfiguredConnectionString(
        IConfiguration configuration,
        string connectionNameKey)
    {
        var connectionName = TryGetConfigurationValue(configuration, connectionNameKey);
        return connectionName is null
            ? null
            : TryGetConnectionString(configuration, connectionName);
    }

    private static string? TryGetConnectionString(
        IConfiguration configuration,
        string name)
    {
        var value = configuration.GetConnectionString(name);
        if (IsConfiguredValue(value))
        {
            return value;
        }

        value = configuration[name];
        return IsConfiguredValue(value) ? value : null;
    }

    private static string? TryGetStorageResourceConnectionString(
        IConfiguration configuration,
        string resourceName)
    {
        var candidates = new[]
        {
            $"{resourceName}:ConnectionString",
            $"{resourceName}__ConnectionString",
            $"{resourceName}_ConnectionString",
            $"{resourceName.ToUpperInvariant()}_CONNECTIONSTRING"
        };

        foreach (var candidate in candidates)
        {
            var value = TryGetConfigurationValue(configuration, candidate);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static string? TryGetConfigurationValue(
        IConfiguration configuration,
        string key)
    {
        var value = configuration[key];
        return IsConfiguredValue(value) ? value : null;
    }

    private static bool IsConfiguredValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !(value.StartsWith("<", StringComparison.Ordinal) && value.EndsWith(">", StringComparison.Ordinal));

    private static string? TryGetServiceBusEntityPath(string? connectionString)
    {
        if (!IsConfiguredValue(connectionString))
        {
            return null;
        }

        var configuredConnectionString = connectionString!;
        foreach (var segment in configuredConnectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = segment[..separatorIndex];
            if (!key.Equals("EntityPath", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = segment[(separatorIndex + 1)..];
            return IsConfiguredValue(value) ? value : null;
        }

        return null;
    }

    private static string? UseDevelopmentFallback(
        IHostEnvironment environment,
        string fallbackValue) =>
        environment.IsDevelopment() ? fallbackValue : null;

    private static string? TryGetAzuriteMappedBlobConnectionString(IHostEnvironment environment)
    {
        if (!environment.IsDevelopment())
        {
            return null;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "ps --format \"{{.Names}}\\t{{.Ports}}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);

            foreach (var line in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!line.StartsWith("emulatedstorage-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var mappingIndex = line.IndexOf("->10000/tcp", StringComparison.OrdinalIgnoreCase);
                if (mappingIndex < 0)
                {
                    continue;
                }

                var prefix = line[..mappingIndex];
                var portSeparatorIndex = prefix.LastIndexOf(':');
                if (portSeparatorIndex < 0)
                {
                    continue;
                }

                var portValue = prefix[(portSeparatorIndex + 1)..];
                if (!int.TryParse(portValue, out var port))
                {
                    continue;
                }

                return
                    $"DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:{port}/devstoreaccount1;";
            }
        }
        catch
        {
            // Fall back to the standard local storage connection string when Docker inspection is unavailable.
        }

        return null;
    }

    private static string? TryGetAzuriteMappedCosmosConnectionString(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        if (!environment.IsDevelopment())
        {
            return null;
        }

        var mappedPort = TryGetDockerMappedPort(
            configuration,
            "AzureCosmos:ResourceName",
            8081);

        return mappedPort is null
            ? null
            : $"AccountEndpoint=http://127.0.0.1:{mappedPort}/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;";
    }

    private static string? NormalizeLocalBlobConnectionString(
        string? connectionString,
        IHostEnvironment environment)
    {
        if (!IsConfiguredValue(connectionString))
        {
            return null;
        }

        if (!environment.IsDevelopment())
        {
            return connectionString;
        }

        var configuredConnectionString = connectionString!;
        if (!configuredConnectionString.Equals(LocalBlobConnectionString, StringComparison.OrdinalIgnoreCase) &&
            !configuredConnectionString.Contains("127.0.0.1:10000", StringComparison.OrdinalIgnoreCase) &&
            !configuredConnectionString.Contains("localhost:10000", StringComparison.OrdinalIgnoreCase))
        {
            return configuredConnectionString;
        }

        return TryGetAzuriteMappedBlobConnectionString(environment) ?? configuredConnectionString;
    }

    private static string? NormalizeLocalCosmosConnectionString(
        string? connectionString,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        if (!IsConfiguredValue(connectionString))
        {
            return null;
        }

        if (!environment.IsDevelopment())
        {
            return connectionString;
        }

        var configuredConnectionString = connectionString!;
        var currentEndpoint = TryGetConnectionStringSegment(configuredConnectionString, "AccountEndpoint");
        if (!Uri.TryCreate(currentEndpoint, UriKind.Absolute, out var endpointUri))
        {
            return configuredConnectionString;
        }

        var mappedPort = TryGetDockerMappedPort(
            configuration,
            GetCosmosResourceLookupKeys(configuration),
            8081);

        if (mappedPort is null || endpointUri.Port == mappedPort.Value)
        {
            return configuredConnectionString;
        }

        var normalizedEndpoint = $"{endpointUri.Scheme}://127.0.0.1:{mappedPort.Value}/";
        return ReplaceConnectionStringSegment(
            configuredConnectionString,
            "AccountEndpoint",
            normalizedEndpoint);
    }

    private static int? TryGetDockerMappedPort(
        IConfiguration configuration,
        string resourceNameKey,
        int containerPort) =>
        TryGetDockerMappedPort(configuration, [resourceNameKey], containerPort);

    private static int? TryGetDockerMappedPort(
        IConfiguration configuration,
        IEnumerable<string> resourceNameKeys,
        int containerPort)
    {
        var resourceName = resourceNameKeys
            .Select(key => TryGetConfigurationValue(configuration, key))
            .FirstOrDefault(IsConfiguredValue);
        if (!IsConfiguredValue(resourceName))
        {
            return null;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "ps --format \"{{.Names}}\\t{{.Ports}}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);

            foreach (var line in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separatorIndex = line.IndexOf('\t');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                var containerName = line[..separatorIndex];
                if (!containerName.StartsWith(resourceName!, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var ports = line[(separatorIndex + 1)..];
                var marker = $"->{containerPort}/tcp";
                var mappingIndex = ports.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (mappingIndex < 0)
                {
                    continue;
                }

                var prefix = ports[..mappingIndex];
                var portSeparatorIndex = prefix.LastIndexOf(':');
                if (portSeparatorIndex < 0)
                {
                    continue;
                }

                var portValue = prefix[(portSeparatorIndex + 1)..];
                if (int.TryParse(portValue, out var mappedPort))
                {
                    return mappedPort;
                }
            }
        }
        catch
        {
            // Fall back to the configured connection string when Docker inspection is unavailable.
        }

        return null;
    }

    private static string[] GetCosmosResourceLookupKeys(IConfiguration configuration)
    {
        var keys = new List<string>
        {
            "AzureCosmos:ResourceName"
        };

        var connectionName = TryGetConfigurationValue(configuration, "AzureCosmos:ConnectionName");
        if (IsConfiguredValue(connectionName))
        {
            keys.Add("AzureCosmos:ConnectionName");
        }

        return keys.ToArray();
    }

    private static string? TryGetConnectionStringSegment(string connectionString, string segmentName)
    {
        foreach (var segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = segment[..separatorIndex];
            if (!key.Equals(segmentName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = segment[(separatorIndex + 1)..];
            return IsConfiguredValue(value) ? value : null;
        }

        return null;
    }

    private static string ReplaceConnectionStringSegment(
        string connectionString,
        string segmentName,
        string replacementValue)
    {
        var segments = connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        for (var index = 0; index < segments.Count; index++)
        {
            var separatorIndex = segments[index].IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = segments[index][..separatorIndex];
            if (!key.Equals(segmentName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            segments[index] = $"{key}={replacementValue}";
            return string.Join(';', segments) + ";";
        }

        segments.Add($"{segmentName}={replacementValue}");
        return string.Join(';', segments) + ";";
    }

    private static string ThrowMissingConnectionString(
        string resourceDisplayName,
        string expectedKeys,
        IHostEnvironment environment)
    {
        if (environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"{resourceDisplayName} connection string is not configured. Expected one of: {expectedKeys}.");
        }

        throw new InvalidOperationException($"{resourceDisplayName} connection string is not configured.");
    }
}
