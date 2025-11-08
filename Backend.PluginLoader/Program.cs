using Backend.Conformation.DAL;
using Backend.Interfaces.Repositories;
using Backend.PluginLoader.Services;
using Backend.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var app = Host.CreateDefaultBuilder()
    .ConfigureAppConfiguration((context, config) =>
        config.AddUserSecrets<Program>(optional: false)
            .AddEnvironmentVariables())
    .ConfigureServices((context, services) =>
    {
        var sqlConnectionString = context.Configuration.GetConnectionString("TasksDB");
        services.AddDbContextPool<WorkflowDbContext>(options =>
            options.UseSqlServer(sqlConnectionString));

        var blobConnectionString = context.Configuration.GetConnectionString("BlobConnection");

        services.AddAzureClients(builder =>
        {
            builder.AddBlobServiceClient(blobConnectionString);
        });

        services.AddScoped<IWorkflowTaskRepository, WorkflowTaskRepository>();
        services.AddHostedService<PluginReceptionService>();
    })
    .Build();

/*builder.Services.AddAzureClients(async clientBuilder =>
{
    // Register clients for each service
    clientBuilder.AddSecretClient(new Uri("<key_vault_url>"));
    clientBuilder.AddBlobServiceClient(new Uri("<storage_url>"));
    clientBuilder.AddServiceBusClientWithNamespace(
        "<your_namespace>.servicebus.windows.net");

    // Set a credential for all clients to use by default
    DefaultAzureCredential credential = new();
clientBuilder.UseCredential(credential);

    // Register a subclient for each Service Bus Queue
    List<string> queueNames = await GetQueueNames(credential);
    foreach (string queue in queueNames)
    {
        clientBuilder.AddClient<ServiceBusSender, ServiceBusClientOptions>(
            (_, _, provider) => provider.GetService<ServiceBusClient>()
                .CreateSender(queue)).WithName(queue);
    }

    // Register a custom client factory
    clientBuilder.AddClient<AzureOpenAIClient, AzureOpenAIClientOptions>(
        (options, _, _) => new AzureOpenAIClient(
            new Uri("<url_here>"), credential, options)); 
});*/
