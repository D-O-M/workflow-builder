using Aspire.Hosting.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

internal class Program
{
    private static void Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var cosmosDb = AddCosmos(builder);

        var password = builder.AddParameter("SqlServerPassword", secret: true);

        var sql = AddSqlServer(builder, password);
        var sqlDb = sql.AddDatabase("TasksDB");

        var pluginsBlobResourceName = builder.Configuration.GetValue<string>(
            "AzureStorage:BlobContainers:Plugins:ResourceName")!;

        var pluginsBlobContainerName = builder.Configuration.GetValue<string>(
            "AzureStorage:BlobContainers:Plugins:ContainerName")!;

        var (storage, blobs, blobContainer) = AddStorage(builder,
            pluginsBlobResourceName,
            pluginsBlobContainerName);

        var (serviceBus, workflowTaskQueue) = AddServiceBus(builder);
        var cosmosConnectionName = GetRequiredConfigurationValue(
            builder.Configuration,
            "AzureCosmos:ConnectionName");
        var workflowTaskQueueConnectionName = GetRequiredConfigurationValue(
            builder.Configuration,
            "AzureServiceBus:Queues:WorkflowTaskQueue:ConnectionName");

        var api = builder.AddProject<Projects.Backend_API>("backend-api")
            .WithReference(cosmosDb, connectionName: cosmosConnectionName)
            .WaitFor(cosmosDb)
            .WithReference(blobs)
            .WaitFor(blobContainer)
            .WithReference(workflowTaskQueue, connectionName: workflowTaskQueueConnectionName)
            .WaitFor(serviceBus)
            .WithReference(sqlDb)
            .WaitFor(sqlDb);

        var engine = builder.AddProject<Projects.Backend_Engine>("backend-engine")
            .WithReference(cosmosDb, connectionName: cosmosConnectionName)
            .WaitFor(cosmosDb)
            .WithReference(blobs)
            .WaitFor(blobContainer)
            .WithReference(workflowTaskQueue, connectionName: workflowTaskQueueConnectionName)
            .WaitFor(serviceBus)
            .WithReference(sqlDb)
            .WaitFor(sqlDb);

        /*builder.AddProject<Projects.Backend_PluginLoader>("plugin-loader")
            .WithReference(blobs)
            .WaitFor(blobContainer)
            .WithReference(workflowTaskQueue)
            .WaitFor(serviceBus)
            .WithReference(sqlDb)
            .WaitFor(sqlDb);*/

        builder.AddAzureFunctionsProject<Projects.Backend_Functions_PluginDetector>("plugin-detector")
            .WithHostStorage(storage)
            .WaitFor(blobContainer)
            .WithReference(sqlDb)
            .WaitFor(sqlDb);

        //AddFrontend(builder, api);

        builder.Build().Run();
    }

    static IResourceBuilder<AzureCosmosDBDatabaseResource> AddCosmos(
        IDistributedApplicationBuilder builder)
    {
        var cosmosResourceName = GetRequiredConfigurationValue(
            builder.Configuration,
            "AzureCosmos:ResourceName");
        var cosmosDatabaseName = GetRequiredConfigurationValue(
            builder.Configuration,
            "AzureCosmos:DatabaseName");
        var persistCosmosData = builder.Configuration.GetValue("AzureCosmos:PersistData", false);

#pragma warning disable ASPIRECOSMOSDB001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        var cosmos = builder.AddAzureCosmosDB(cosmosResourceName)
            .RunAsPreviewEmulator(emulator =>
            {
                emulator.WithDataExplorer(1234);
                emulator.WithGatewayPort(8081);
                emulator.WithLifetime(
                    persistCosmosData
                        ? ContainerLifetime.Persistent
                        : ContainerLifetime.Session);

                if (persistCosmosData)
                {
                    emulator.WithDataVolume();
                }
            });
#pragma warning restore ASPIRECOSMOSDB001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

        var database = cosmos.AddCosmosDatabase(cosmosDatabaseName);
        database.AddContainer("workflows", "/blueprintId");
        database.AddContainer("tasks", "/workflowId");

        return database;
    }

    static IResourceBuilder<SqlServerServerResource> AddSqlServer(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<ParameterResource> password)
    {
        var persistSqlData = builder.Configuration.GetValue("SqlServer:PersistData", true);

        var sqlServer = builder.AddSqlServer("sqlserver", password)
            .WithHostPort(12345)
            .WithLifetime(
                persistSqlData
                    ? ContainerLifetime.Persistent
                    : ContainerLifetime.Session)
            .WithEnvironment("ACCEPT_EULA", "Y")
            .WithEnvironment("MSSQL_SA_PASSWORD", password);

        if (persistSqlData)
        {
            sqlServer.WithDataVolume();
        }

        return sqlServer;
    }
    //.WithEndpoint(port: 1433, name: "sqlserver-tcp", targetPort: 1433);

    static (
        IResourceBuilder<AzureStorageResource> storage,
        IResourceBuilder<AzureBlobStorageResource> blobStorage,
        IResourceBuilder<AzureBlobStorageContainerResource> blobContainer)
        AddStorage(IDistributedApplicationBuilder builder,
            string pluginsBlobResourceName,
            string pluginsBlobContainerName)
    {
        var azureStorage = builder.AddAzureStorage(
            builder.Configuration.GetValue<string>("AzureStorage:ResourceName")!);

        if (builder.Environment.IsDevelopment())
        {
            azureStorage.RunAsEmulator(config =>
                config.WithLifetime(ContainerLifetime.Persistent));
        }

        var blobs = azureStorage.AddBlobs("BlobConnection");

        var blobContainer = azureStorage.AddBlobContainer(
            pluginsBlobResourceName,
            pluginsBlobContainerName);

        return (azureStorage, blobs, blobContainer);
    }

    static (IResourceBuilder<AzureServiceBusResource> serviceBus,
        IResourceBuilder<AzureServiceBusQueueResource> workflowTaskQueue)
        AddServiceBus(IDistributedApplicationBuilder builder)
    {
        var serviceBusResourceName = GetRequiredConfigurationValue(
            builder.Configuration,
            "AzureServiceBus:ResourceName");
        var workflowTaskQueueResourceName = GetRequiredConfigurationValue(
            builder.Configuration,
            "AzureServiceBus:Queues:WorkflowTaskQueue:ResourceName");
        var workflowTaskQueueName = GetRequiredConfigurationValue(
            builder.Configuration,
            "AzureServiceBus:Queues:WorkflowTaskQueue:Name");

        var serviceBus = builder.AddAzureServiceBus(serviceBusResourceName);

        if (builder.Environment.IsDevelopment())
        {
            serviceBus.RunAsEmulator();
        }

        var workflowTaskQueue = serviceBus.AddServiceBusQueue(
            workflowTaskQueueResourceName,
            workflowTaskQueueName);

        return (serviceBus, workflowTaskQueue);
    }

    static string GetRequiredConfigurationValue(
        IConfiguration configuration,
        string key) =>
        configuration.GetValue<string>(key)
        ?? throw new InvalidOperationException($"Configuration value '{key}' is not configured.");

    static void AddFrontend(IDistributedApplicationBuilder builder, IResourceBuilder<ProjectResource> api)
    {
        var uiDirectoryPath = Path.Combine(Directory.GetParent(Environment.CurrentDirectory)!.FullName, "UI");

        var frontend = builder.AddDockerfile("frontend", uiDirectoryPath)
            .WithHttpEndpoint(4200, name: "frontend-http", targetPort: 4200);

        MountUiRootFilesAndTopDirs(frontend, uiDirectoryPath, "/app");

        frontend.WithReference(api)
            .WaitFor(api);
    }

    static void MountUiRootFilesAndTopDirs(
            IResourceBuilder<ContainerResource> container,
            string sourceRoot,
            string targetRoot = "/app")
    {
        // Combine top-level files and directories into a single list
        var entries = Directory.GetFileSystemEntries(sourceRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(static entry =>
            {
                var name = Path.GetFileName(entry).ToLowerInvariant();
                var exclusions = new string[] { "package-lock.json", "node_modules", "dockerfile" };

                // Exclude node_modules and hidden entries
                if (name.StartsWith("."))
                    return false;

                if (exclusions.Any(e => e == name))
                    return false;

                // Exclude by extension or exact name
                var extension = Path.GetExtension(entry);
                return !extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
                       && !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase);
            });

        // Mount each entry (file or directory)
        foreach (var entry in entries)
        {
            var relativeName = Path.GetFileName(entry);
            var targetPath = Path.Combine(targetRoot, relativeName).Replace("\\", "/");

            container.WithBindMount(entry, targetPath, isReadOnly: false);
        }
    }
}
