using Microsoft.Extensions.Configuration;

namespace Backend.Topology;

public sealed class WorkflowBuilderTopologyValidationException : InvalidOperationException
{
    public WorkflowBuilderTopologyValidationException(string serviceName, IReadOnlyList<string> errors)
        : base(BuildMessage(serviceName, errors))
    {
        ServiceName = serviceName;
        Errors = errors;
    }

    public string ServiceName { get; }

    public IReadOnlyList<string> Errors { get; }

    private static string BuildMessage(string serviceName, IReadOnlyList<string> errors) =>
        $"WorkflowBuilder topology validation failed for {serviceName}: {string.Join("; ", errors)}";
}

public static class WorkflowBuilderTopologyValidator
{
    public static void ValidateApi(IConfiguration configuration)
    {
        var errors = new List<string>();
        RequireSql(errors, configuration);
        RequireCosmos(errors, configuration);
        RequireCosmosDatabase(errors, configuration, WorkflowBuilderTopology.Cosmos.DatabaseNameKey);
        RequireQueue(errors, configuration, WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep);
        RequireAbsoluteUri(errors, configuration, WorkflowBuilderTopology.ClientConfig.SignalRBaseUrlKey);
        RequireAbsoluteUri(errors, configuration, WorkflowBuilderTopology.ClientConfig.GrafanaBaseUrlKey);
        ThrowIfAny("API", errors);
    }

    public static void ValidateEngine(IConfiguration configuration)
    {
        var errors = new List<string>();
        RequireSql(errors, configuration);
        RequireCosmos(errors, configuration);
        RequireCosmosDatabase(errors, configuration, WorkflowBuilderTopology.Cosmos.DatabaseNameKey);
        RequireBlobConnection(errors, configuration);
        RequireBlobContainer(errors, configuration, WorkflowBuilderTopology.Storage.BlobContainers.Plugins);
        RequireBlobContainer(errors, configuration, WorkflowBuilderTopology.Storage.BlobContainers.Artifacts);
        RequireQueue(errors, configuration, WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep);
        RequireQueue(errors, configuration, WorkflowBuilderTopology.ServiceBus.Queues.PluginControl);
        ThrowIfAny("Engine", errors);
    }

    public static void ValidatePluginDetector(IConfiguration configuration)
    {
        var errors = new List<string>();
        RequireSql(errors, configuration);
        RequireConfigurationValue(errors, configuration, WorkflowBuilderTopology.Storage.AzureWebJobsStorageKey);
        RequireBlobContainer(errors, configuration, WorkflowBuilderTopology.Storage.BlobContainers.Plugins);
        RequireAliasMatch(
            errors,
            configuration,
            WorkflowBuilderTopology.Storage.BlobContainers.Plugins.LegacyAliasKey,
            WorkflowBuilderTopology.Storage.BlobContainers.Plugins.ContainerNameKey);
        RequireQueue(errors, configuration, WorkflowBuilderTopology.ServiceBus.Queues.PluginControl);
        ThrowIfAny("PluginDetector", errors);
    }

    public static void ValidateProgressBroadcaster(IConfiguration configuration)
    {
        var errors = new List<string>();
        RequireSql(errors, configuration);
        RequireCosmos(errors, configuration);
        RequireConnectionString(errors, configuration, WorkflowBuilderTopology.Realtime.RealtimeCosmosConnectionName);
        RequireAbsoluteUri(errors, configuration, WorkflowBuilderTopology.Realtime.SignalRBaseUrlKey);
        RequireExactValue(
            errors,
            configuration,
            WorkflowBuilderTopology.Realtime.CosmosDatabaseNameKey,
            WorkflowBuilderTopology.Cosmos.DefaultDatabaseName);
        RequireExactValue(
            errors,
            configuration,
            WorkflowBuilderTopology.Realtime.ProcessesContainerNameKey,
            WorkflowBuilderTopology.Cosmos.Containers.Processes.Name);
        RequireExactValue(
            errors,
            configuration,
            WorkflowBuilderTopology.Realtime.ProcessStepsContainerNameKey,
            WorkflowBuilderTopology.Cosmos.Containers.ProcessSteps.Name);
        RequireExactValue(
            errors,
            configuration,
            WorkflowBuilderTopology.Realtime.LeaseContainerNameKey,
            WorkflowBuilderTopology.Cosmos.Containers.ProcessStepProgressLeases.Name);
        ValidateOptionalBoolean(errors, configuration, WorkflowBuilderTopology.Realtime.UseHostedChangeFeedKey);
        ThrowIfAny("ProgressBroadcaster", errors);
    }

    public static void ValidateSignalR()
    {
        var errors = new List<string>();
        if (!WorkflowBuilderTopology.Realtime.HubRoute.StartsWith("/", StringComparison.Ordinal))
        {
            errors.Add($"{WorkflowBuilderTopology.Realtime.HubRoute} must be an absolute hub route.");
        }

        RequireDistinctStaticValues(
            errors,
            "Realtime group prefixes",
            WorkflowBuilderTopology.Realtime.BlueprintGroupPrefix,
            WorkflowBuilderTopology.Realtime.ProcessGroupPrefix);

        RequireStaticValue(errors, nameof(WorkflowBuilderTopology.Realtime.ProcessSummaryUpdatedEvent), WorkflowBuilderTopology.Realtime.ProcessSummaryUpdatedEvent);
        RequireStaticValue(errors, nameof(WorkflowBuilderTopology.Realtime.ProcessStepsUpdatedEvent), WorkflowBuilderTopology.Realtime.ProcessStepsUpdatedEvent);
        RequireStaticValue(errors, nameof(WorkflowBuilderTopology.Realtime.InternalSubscriptionSnapshotRoute), WorkflowBuilderTopology.Realtime.InternalSubscriptionSnapshotRoute);
        RequireStaticValue(errors, nameof(WorkflowBuilderTopology.Realtime.InternalProcessEventsRoute), WorkflowBuilderTopology.Realtime.InternalProcessEventsRoute);
        ThrowIfAny("SignalR", errors);
    }

    private static void RequireSql(List<string> errors, IConfiguration configuration)
    {
        if (HasConnectionString(configuration, WorkflowBuilderTopology.Sql.TasksDbConnectionName) ||
            HasConnectionString(configuration, WorkflowBuilderTopology.Sql.SqlServerConnectionName))
        {
            return;
        }

        errors.Add(
            $"Missing SQL connection string. Expected ConnectionStrings:{WorkflowBuilderTopology.Sql.TasksDbConnectionName} or ConnectionStrings:{WorkflowBuilderTopology.Sql.SqlServerConnectionName}.");
    }

    private static void RequireCosmos(List<string> errors, IConfiguration configuration)
    {
        if (HasConfiguredValue(configuration[WorkflowBuilderTopology.Cosmos.ConnectionStringKey]))
        {
            return;
        }

        var connectionName = GetConfiguredValue(configuration, WorkflowBuilderTopology.Cosmos.ConnectionNameKey);
        if (connectionName is not null && HasConnectionString(configuration, connectionName))
        {
            return;
        }

        if (HasConnectionString(configuration, WorkflowBuilderTopology.Cosmos.DefaultDatabaseName))
        {
            return;
        }

        errors.Add(
            $"Missing Cosmos connection. Expected {WorkflowBuilderTopology.Cosmos.ConnectionStringKey}, {WorkflowBuilderTopology.Cosmos.ConnectionNameKey} plus a matching connection string, or ConnectionStrings:{WorkflowBuilderTopology.Cosmos.DefaultDatabaseName}.");
    }

    private static void RequireCosmosDatabase(List<string> errors, IConfiguration configuration, string key) =>
        RequireExactValue(errors, configuration, key, WorkflowBuilderTopology.Cosmos.DefaultDatabaseName);

    private static void RequireBlobConnection(List<string> errors, IConfiguration configuration)
    {
        if (HasConnectionString(configuration, WorkflowBuilderTopology.Storage.BlobConnectionName) ||
            HasConfiguredValue(configuration[WorkflowBuilderTopology.Storage.ConnectionStringKey]) ||
            HasConfiguredValue(configuration[$"{WorkflowBuilderTopology.Storage.BlobConnectionName}:ConnectionString"]))
        {
            return;
        }

        errors.Add(
            $"Missing blob storage connection. Expected ConnectionStrings:{WorkflowBuilderTopology.Storage.BlobConnectionName} or {WorkflowBuilderTopology.Storage.ConnectionStringKey}.");
    }

    private static void RequireConnectionString(List<string> errors, IConfiguration configuration, string connectionName)
    {
        if (HasConnectionString(configuration, connectionName))
        {
            return;
        }

        errors.Add($"Missing connection string. Expected ConnectionStrings:{connectionName}.");
    }

    private static void RequireBlobContainer(List<string> errors, IConfiguration configuration, BlobContainer container)
    {
        RequireExactValue(errors, configuration, container.ContainerNameKey, container.ContainerName);
        RequireAliasMatch(errors, configuration, container.LegacyAliasKey, container.ContainerNameKey);
    }

    private static void RequireQueue(List<string> errors, IConfiguration configuration, ServiceBusQueue queue)
    {
        var connectionName = GetConfiguredValue(configuration, queue.ConnectionNameKey);
        if (connectionName is null)
        {
            errors.Add($"Missing {queue.LogicalName} connection name at {queue.ConnectionNameKey}.");
        }
        else if (!HasConnectionString(configuration, connectionName))
        {
            errors.Add($"Missing Service Bus connection string for {queue.LogicalName}. Expected ConnectionStrings:{connectionName}.");
        }

        RequireExactValue(errors, configuration, queue.QueueNameKey, queue.QueueName);

        var legacyValue = GetConfiguredValue(configuration, queue.LegacyQueueNameKey);
        if (legacyValue is not null && !string.Equals(legacyValue, queue.QueueName, StringComparison.Ordinal))
        {
            errors.Add($"{queue.LegacyQueueNameKey} must match {queue.QueueNameKey} value '{queue.QueueName}' when both are configured.");
        }
    }

    private static void RequireAbsoluteUri(List<string> errors, IConfiguration configuration, string key)
    {
        var value = GetConfiguredValue(configuration, key);
        if (value is null)
        {
            errors.Add($"Missing required URI setting {key}.");
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            errors.Add($"{key} must be an absolute URI.");
        }
    }

    private static void RequireConfigurationValue(List<string> errors, IConfiguration configuration, string key)
    {
        if (!HasConfiguredValue(configuration[key]))
        {
            errors.Add($"Missing required setting {key}.");
        }
    }

    private static void RequireExactValue(List<string> errors, IConfiguration configuration, string key, string expected)
    {
        var value = GetConfiguredValue(configuration, key);
        if (value is null)
        {
            errors.Add($"Missing required setting {key}; expected '{expected}'.");
            return;
        }

        if (!string.Equals(value, expected, StringComparison.Ordinal))
        {
            errors.Add($"{key} must be '{expected}', but was '{value}'.");
        }
    }

    private static void RequireAliasMatch(List<string> errors, IConfiguration configuration, string? aliasKey, string canonicalKey)
    {
        if (aliasKey is null)
        {
            return;
        }

        var aliasValue = GetConfiguredValue(configuration, aliasKey);
        if (aliasValue is null)
        {
            return;
        }

        var canonicalValue = GetConfiguredValue(configuration, canonicalKey);
        if (!string.Equals(aliasValue, canonicalValue, StringComparison.Ordinal))
        {
            errors.Add($"{aliasKey} must match {canonicalKey} when both are configured.");
        }
    }

    private static void ValidateOptionalBoolean(List<string> errors, IConfiguration configuration, string key)
    {
        var value = GetConfiguredValue(configuration, key);
        if (value is not null && !bool.TryParse(value, out _))
        {
            errors.Add($"{key} must be 'true' or 'false' when configured.");
        }
    }

    private static void RequireStaticValue(List<string> errors, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{name} must be configured.");
        }
    }

    private static void RequireDistinctStaticValues(List<string> errors, string name, string first, string second)
    {
        if (string.Equals(first, second, StringComparison.Ordinal))
        {
            errors.Add($"{name} must be distinct.");
        }
    }

    private static bool HasConnectionString(IConfiguration configuration, string name) =>
        HasConfiguredValue(configuration[$"ConnectionStrings:{name}"]) ||
        HasConfiguredValue(configuration[name]);

    private static string? GetConfiguredValue(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return HasConfiguredValue(value) ? value!.Trim() : null;
    }

    private static bool HasConfiguredValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return !(trimmed.StartsWith("<", StringComparison.Ordinal) &&
            trimmed.EndsWith(">", StringComparison.Ordinal));
    }

    private static void ThrowIfAny(string serviceName, List<string> errors)
    {
        if (errors.Count > 0)
        {
            throw new WorkflowBuilderTopologyValidationException(serviceName, errors);
        }
    }
}
