using Microsoft.Extensions.Hosting;

internal class Program
{
    private static void Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var cosmos = builder.AddContainer("cosmosdb", "mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator", "vnext-preview")
            //.WithEndpoint("https", 8081, scheme: "https") // Cosmos uses HTTPS
            .WithEnvironment("AZURE_COSMOS_EMULATOR_PARTITION_COUNT", "3")
            .WithEnvironment("AZURE_COSMOS_EMULATOR_ENABLE_DATA_PERSISTENCE", "true")
            .WithEnvironment("AZURE_COSMOS_EMULATOR_IP_ADDRESS_OVERRIDE", "127.0.0.1")
            .WithVolume("cosmos-data", "/cosmos-data")
            .WithHttpEndpoint(8080, name: "cosmos-ui-http", targetPort: 1234)
            .WithHttpsEndpoint(8081, name: "cosmos-ui-https", targetPort: 2345);

        var password = builder.AddParameter("SqlServerPassword", secret: true);

        var sql = builder.AddSqlServer("sqlserver", password)
            .WithLifetime(ContainerLifetime.Persistent)
            .WithEnvironment("ACCEPT_EULA", "Y")
            .WithEnvironment("MSSQL_SA_PASSWORD", password);
            //.WithEndpoint(port: 1433, name: "sqlserver-tcp", targetPort: 1433);

        var sqlDb = sql.AddDatabase("TasksDB");
        //.WithEnvironment("SA_PASSWORD", password)
        //.WithVolume("sqldata", "/var/opt/mssql/data");

        var uiDirectoryPath = Path.Combine(Directory.GetParent(Environment.CurrentDirectory)!.FullName, "UI");

        var frontend = builder.AddDockerfile("frontend", uiDirectoryPath)
            .WithHttpEndpoint(4200, name: "frontend-http", targetPort: 4200);

        //var azurite = builder.AddContainer("azurite", "mcr.microsoft.com/azure-storage/azurite")
        //    .WithImageTag("latest")
        //    .WithEndpoint(port: 10000, targetPort: 10000)
        //    .WithVolume("blob-data, /blob-data");

        var storage = builder.AddAzureStorage("Storage");

        if (builder.Environment.IsDevelopment())
        {
            storage.RunAsEmulator();
        }

        var blobs = storage.AddBlobs("BlobConnection");
        var queues = storage.AddQueues("QueueConnection");

        MountUiRootFilesAndTopDirs(frontend, uiDirectoryPath, "/app");

        var api = builder.AddProject<Projects.Backend_API>("backend-api")
            .WithReference(blobs)
            .WaitFor(blobs)
            .WithReference(queues)
            .WaitFor(queues)
            .WithReference(sqlDb)
            .WaitFor(sqlDb);

        builder.AddProject<Projects.Backend_PluginLoader>("plugin-loader")
            .WithReference(blobs)
            .WaitFor(blobs)
            .WithReference(queues)
            .WaitFor(queues)
            .WithReference(sqlDb)
            .WaitFor(sqlDb);

        frontend.WithReference(api)
            .WaitFor(api);

        builder.Build().Run();
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
