using Aspire.Hosting.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

internal class Program
{
    private static void Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var cosmos = AddCosmos(builder);

        var password = builder.AddParameter("SqlServerPassword", secret: true);

        var sql = AddSqlServer(builder, password);
        var sqlDb = sql.AddDatabase("TasksDB");

        var (storage, blobs, blobContainer) = AddStorage(builder);
        var (serviceBus, workflowTaskQueue) = AddServiceBus(builder);

        var api = builder.AddProject<Projects.Backend_API>("backend-api")
            .WithReference(blobs)
            .WaitFor(blobContainer)
            .WithReference(workflowTaskQueue)
            .WaitFor(serviceBus)
            .WithReference(sqlDb)
            .WaitFor(sqlDb);

        var engine = builder.AddProject<Projects.Backend_Engine>("backend-engine")
            .WithReference(blobs)
            .WaitFor(blobContainer)
            .WithReference(workflowTaskQueue)
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
            .WithReference(blobs);

        AddFrontend(builder, api);

        builder.Build().Run();
    }

    static IResourceBuilder<ContainerResource> AddCosmos(IDistributedApplicationBuilder builder) =>
        builder.AddContainer("cosmosdb", "mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator", "vnext-preview")
            //.WithEndpoint("https", 8081, scheme: "https") // Cosmos uses HTTPS
            .WithEnvironment("AZURE_COSMOS_EMULATOR_PARTITION_COUNT", "3")
            .WithEnvironment("AZURE_COSMOS_EMULATOR_ENABLE_DATA_PERSISTENCE", "true")
            .WithEnvironment("AZURE_COSMOS_EMULATOR_IP_ADDRESS_OVERRIDE", "127.0.0.1")
            .WithVolume("cosmos-data", "/cosmos-data")
            .WithHttpEndpoint(8080, name: "cosmos-ui-http", targetPort: 1234)
            .WithHttpsEndpoint(8081, name: "cosmos-ui-https", targetPort: 2345);

    static IResourceBuilder<SqlServerServerResource> AddSqlServer(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<ParameterResource> password) =>
        builder.AddSqlServer("sqlserver", password)
            .WithLifetime(ContainerLifetime.Persistent)
            .WithEnvironment("ACCEPT_EULA", "Y")
            .WithEnvironment("MSSQL_SA_PASSWORD", password);
    //.WithEndpoint(port: 1433, name: "sqlserver-tcp", targetPort: 1433);

    static (
        IResourceBuilder<AzureStorageResource> storage,
        IResourceBuilder<AzureBlobStorageResource> blobStorage,
        IResourceBuilder<AzureBlobStorageContainerResource> blobContainer)
        AddStorage(IDistributedApplicationBuilder builder)
    {
        var azureStorage = builder.AddAzureStorage(
            builder.Configuration.GetValue<string>("AzureStorage:ResourceName")!);

        if (builder.Environment.IsDevelopment())
        {
            azureStorage.RunAsEmulator();
        }

        var blobs = azureStorage.AddBlobs("BlobConnection");

        var pluginsBlobResourceName = builder.Configuration.GetValue<string>(
            "AzureStorage:BlobContainers:Plugins:ResourceName")!;
        var pluginsBlobContainerName = builder.Configuration.GetValue<string>(
            "AzureStorage:BlobContainers:Plugins:ContainerName")!;

        var blobContainer = azureStorage.AddBlobContainer(
            pluginsBlobResourceName,
            pluginsBlobContainerName);

        return (azureStorage, blobs, blobContainer);
    }

    static (IResourceBuilder<AzureServiceBusResource> serviceBus,
        IResourceBuilder<AzureServiceBusQueueResource> workflowTaskQueue)
        AddServiceBus(IDistributedApplicationBuilder builder)
    {
        var serviceBus = builder.AddAzureServiceBus(
            builder.Configuration.GetValue<string>("AzureServiceBus:ResourceName")!);

        if (builder.Environment.IsDevelopment())
        {
            serviceBus.RunAsEmulator();
        }

        var serviceBusResourceName = builder.Configuration.GetValue<string>(
            "AzureServiceBus:ResourceName");

        var workflowTaskQueueResourceName = builder.Configuration.GetValue<string>(
            "AzureServiceBus:Queues:WorkflowTaskQueue:ResourceName")!;
        var workflowTaskQueueName = builder.Configuration.GetValue<string>(
            "AzureServiceBus:Queues:WorkflowTaskQueue:Name")!;

        var workflowTaskQueue = serviceBus.AddServiceBusQueue(
            workflowTaskQueueResourceName, workflowTaskQueueName);

        return (serviceBus, workflowTaskQueue);
    }

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
