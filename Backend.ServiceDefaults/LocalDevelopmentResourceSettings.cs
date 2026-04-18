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
        TryGetConfiguredConnectionString(configuration, "AzureCosmos:ConnectionName")
        ?? TryGetConnectionString(configuration, "enginedb")
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
        IHostEnvironment environment) =>
        TryGetConnectionString(configuration, "BlobConnection")
        ?? UseDevelopmentFallback(environment, LocalBlobConnectionString)
        ?? ThrowMissingConnectionString(
            "blob storage",
            "BlobConnection",
            environment);

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
        return IsConfiguredValue(value) ? value : null;
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
