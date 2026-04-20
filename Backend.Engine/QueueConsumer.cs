using Backend.Interfaces.Queues;
using Backend.Interfaces.Repositories;

namespace Backend.Engine;

public class QueueConsumer : BackgroundService
{
    private readonly IAgentManager _agentManager;
    private readonly JobManager _jobManager;
    private readonly PluginRuntimeOrchestrator _pluginRuntimeOrchestrator;
    private readonly IProcessStepQueueReader _processStepQueueReader;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger _logger;

    public QueueConsumer(
        IAgentManager agentManager,
        JobManager jobManager,
        PluginRuntimeOrchestrator pluginRuntimeOrchestrator,
        IProcessStepQueueReader processStepQueueReader,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<QueueConsumer> logger)
    {
        _agentManager = agentManager;
        _jobManager = jobManager;
        _pluginRuntimeOrchestrator = pluginRuntimeOrchestrator;
        _processStepQueueReader = processStepQueueReader;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var message = await _processStepQueueReader.ReceiveAsync(stoppingToken);

            if (message is null)
            {
                continue;
            }

            try
            {
                var job = message.Payload;

                await using var scope = _serviceScopeFactory.CreateAsyncScope();

                var blueprintRepository = scope.ServiceProvider
                    .GetRequiredService<IBlueprintRepository>();

                var executableBlueprint = await blueprintRepository
                    .GetFullBlueprintById(job.ExecutableBlueprintId, stoppingToken);

                var blueprintNode = executableBlueprint.AllNodes
                    .FirstOrDefault(node => node.Id == job.BlueprintNodeId);

                if (blueprintNode is null)
                {
                    await message.AbandonAsync(stoppingToken);
                    continue;
                }

                if (!_pluginRuntimeOrchestrator.TryResolveTaskVersion(
                        blueprintNode.TaskDefinitionId,
                        blueprintNode.AllowedMajor,
                        blueprintNode.AllowedMinor,
                        out var runtimeVersion))
                {
                    await _pluginRuntimeOrchestrator.ReloadAsync(stoppingToken);

                    if (!_pluginRuntimeOrchestrator.TryResolveTaskVersion(
                            blueprintNode.TaskDefinitionId,
                            blueprintNode.AllowedMajor,
                            blueprintNode.AllowedMinor,
                            out runtimeVersion))
                    {
                        await message.AbandonAsync(stoppingToken);
                        continue;
                    }
                }

                blueprintNode.ResolvedTaskVersionId = runtimeVersion.Id;
                blueprintNode.ResolvedVersion = runtimeVersion.Version;

                if (!_agentManager.AgentExists(blueprintNode.ResolvedTaskVersionId))
                {
                    await message.AbandonAsync(stoppingToken);
                    continue;
                }

                var context = ActivatorUtilities.CreateInstance<JobContext>(
                    scope.ServiceProvider,
                    job,
                    message,
                    executableBlueprint);

                var started = await _jobManager.StartJob(context, stoppingToken);

                if (!started)
                {
                    await message.AbandonAsync(stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process process-step message {MessageId}", message.MessageId);
                await message.AbandonAsync(stoppingToken);
            }
        }
    }
}
