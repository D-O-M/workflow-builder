using System.Text.Json;
using Backend.API.Infrastructure;
using Backend.Database.Relational;
using Backend.Engine;
using Backend.Engine.Debugging;
using Backend.Interfaces.Queues;
using Backend.Interfaces.Repositories;
using Backend.Interfaces.Utilities;
using Backend.Utilities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace WorkflowBuilder.IntegrationTests.Infrastructure;

internal sealed class WorkflowApiFactory : WebApplicationFactory<Backend.API.Program>
{
    private readonly string _sqliteConnectionString;
    private readonly string _pluginStateRootPath;
    private readonly InMemoryTaskInstanceRepository _taskRepository;
    private readonly InMemoryWorkflowInstanceRepository _workflowRepository;
    private readonly InMemoryWorkflowTaskQueue _queue;
    private readonly InMemoryPluginReloadQueue _pluginReloadQueue;
    private readonly IPluginBundleSource _pluginBundleSource;

    public WorkflowApiFactory(
        string sqliteConnectionString,
        string pluginStateRootPath,
        InMemoryTaskInstanceRepository taskRepository,
        InMemoryWorkflowInstanceRepository workflowRepository,
        InMemoryWorkflowTaskQueue queue,
        InMemoryPluginReloadQueue pluginReloadQueue,
        IPluginBundleSource pluginBundleSource)
    {
        _sqliteConnectionString = sqliteConnectionString;
        _pluginStateRootPath = pluginStateRootPath;
        _taskRepository = taskRepository;
        _workflowRepository = workflowRepository;
        _queue = queue;
        _pluginReloadQueue = pluginReloadQueue;
        _pluginBundleSource = pluginBundleSource;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TasksDB"] = _sqliteConnectionString,
                ["AzureCosmos:ConnectionName"] = "enginedb",
                ["ConnectionStrings:enginedb"] = "AccountEndpoint=https://localhost:1/;AccountKey=fake==;",
                ["AzureServiceBus:Queues:WorkflowTaskQueue:ConnectionName"] = "workflow-task-queue",
                ["ConnectionStrings:workflow-task-queue"] = "Endpoint=sb://localhost/;SharedAccessKeyName=fake;SharedAccessKey=fake=",
                ["AzureServiceBus:Queues:WorkflowTaskQueue:Name"] = "workflowtasks",
                ["AzureServiceBus:Queues:PluginControlQueue:ConnectionName"] = "plugin-control-queue",
                ["ConnectionStrings:plugin-control-queue"] = "Endpoint=sb://localhost/;SharedAccessKeyName=fake;SharedAccessKey=fake=",
                ["AzureServiceBus:Queues:PluginControlQueue:Name"] = "pluginreloadqueue",
                ["PluginStateStore:Mode"] = "File",
                ["PluginStateStore:RootPath"] = _pluginStateRootPath
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IWorkflowDbContextOptionsConfigurator>();
            services.RemoveAll<Microsoft.Azure.Cosmos.CosmosClient>();
            services.RemoveAll<ITaskInstanceRepository>();
            services.RemoveAll<IWorkflowInstanceRepository<Dictionary<string, JsonElement>>>();
            services.RemoveAll<Azure.Messaging.ServiceBus.ServiceBusClient>();
            services.RemoveAll<Azure.Messaging.ServiceBus.ServiceBusSender>();
            services.RemoveAll<IWorkflowTaskQueueReader>();
            services.RemoveAll<IWorkflowTaskQueueWriter>();
            services.RemoveAll<IPluginReloadQueueReader>();
            services.RemoveAll<IPluginReloadQueueWriter>();
            services.RemoveAll<IPluginBundleSource>();

            services.AddSingleton<IWorkflowDbContextOptionsConfigurator>(
                new SqliteWorkflowDbContextOptionsConfigurator(_sqliteConnectionString));
            services.AddSingleton<ITaskInstanceRepository>(_taskRepository);
            services.AddSingleton<IWorkflowInstanceRepository<Dictionary<string, JsonElement>>>(_workflowRepository);
            services.AddSingleton<IWorkflowTaskQueueReader>(_queue);
            services.AddSingleton<IWorkflowTaskQueueWriter>(_queue);
            services.AddSingleton<IPluginReloadQueueReader>(_pluginReloadQueue);
            services.AddSingleton<IPluginReloadQueueWriter>(_pluginReloadQueue);
            services.AddSingleton(_pluginBundleSource);
            services.AddSingleton<IAgentManager, AgentManager>();
            services.AddSingleton<IChildProcessTracker, ChildProcessTracker>();
            services.AddSingleton<IOperatingSystemInformation, TestOperatingSystemInformation>();
            services.AddTransient<NanoServiceAgent>();
            services.AddSingleton<JobManager>();
            services.AddTransient<JobFinalizationService>();
            services.AddSingleton<IPluginWorkspaceFactoryUtility, PluginWorkspaceFactoryUtility>();
            services.AddSingleton<NanoServiceOrchestrator>();
            services.AddHostedService(provider => provider.GetRequiredService<NanoServiceOrchestrator>());
            services.AddHostedService<QueueConsumer>();
            services.AddHostedService<PluginReloadConsumer>();
        });
    }
}
