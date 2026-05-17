using Backend.Interfaces.Queues;
using Backend.Interfaces.Repositories;
using Backend.Models;

namespace Backend.Engine;

public class QueueConsumer : BackgroundService
{
    private readonly IAgentManager _agentManager;
    private readonly IBlueprintExecutionCache _blueprintExecutionCache;
    private readonly JobManager _jobManager;
    private readonly PluginRuntimeOrchestrator _pluginRuntimeOrchestrator;
    private readonly ProcessLifecycleTelemetry _processLifecycleTelemetry;
    private readonly IProcessStepQueueProcessor _processStepQueueProcessor;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger _logger;

    public QueueConsumer(
        IAgentManager agentManager,
        IBlueprintExecutionCache blueprintExecutionCache,
        JobManager jobManager,
        PluginRuntimeOrchestrator pluginRuntimeOrchestrator,
        ProcessLifecycleTelemetry processLifecycleTelemetry,
        IProcessStepQueueProcessor processStepQueueProcessor,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<QueueConsumer> logger)
    {
        _agentManager = agentManager;
        _blueprintExecutionCache = blueprintExecutionCache;
        _jobManager = jobManager;
        _pluginRuntimeOrchestrator = pluginRuntimeOrchestrator;
        _processLifecycleTelemetry = processLifecycleTelemetry;
        _processStepQueueProcessor = processStepQueueProcessor;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Process-step queue consumer starting with Service Bus processor concurrency.");

        await _processStepQueueProcessor.StartProcessingAsync(
            ProcessMessageAsync,
            stoppingToken);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during shutdown.
        }
        finally
        {
            await _processStepQueueProcessor.StopProcessingAsync(CancellationToken.None);
        }
    }

    private async Task ProcessMessageAsync(
        IProcessStepQueueMessage message,
        CancellationToken stoppingToken)
    {
        try
        {
            var job = message.Payload;
            var receivedAt = DateTimeOffset.UtcNow;

            await using var scope = _serviceScopeFactory.CreateAsyncScope();

            var executableBlueprint = await _blueprintExecutionCache
                .GetExecutableBlueprintAsync(job.ExecutableBlueprintId, stoppingToken);

            var blueprintNode = executableBlueprint.AllNodes
                .FirstOrDefault(node => node.Id == job.BlueprintNodeId);

            if (blueprintNode is null)
            {
                await message.AbandonAsync(stoppingToken);
                return;
            }

            var finalizationRepository = scope.ServiceProvider.GetRequiredService<IProcessStepFinalizationRepository>();
            var finalization = await finalizationRepository.GetAsync(
                job.ProcessStepId,
                stoppingToken);
            if (finalization is not null)
            {
                if (finalization.ResolvedTaskVersionId is int resolvedTaskVersionId)
                {
                    blueprintNode.ResolvedTaskVersionId = resolvedTaskVersionId;
                }

                if (!string.IsNullOrWhiteSpace(finalization.ResolvedTaskVersion) &&
                    Version.TryParse(finalization.ResolvedTaskVersion, out var resolvedVersion))
                {
                    blueprintNode.ResolvedVersion = resolvedVersion;
                }

                var resumeContext = ActivatorUtilities.CreateInstance<JobContext>(
                    scope.ServiceProvider,
                    job,
                    message,
                    executableBlueprint);
                _processLifecycleTelemetry.RecordQueueWait(resumeContext, message.EnqueuedAt, receivedAt);
                await scope.ServiceProvider.GetRequiredService<JobFinalizationService>()
                    .ResumeFinalizationAsync(resumeContext, finalization, stoppingToken);
                return;
            }

            if (blueprintNode.NodeKind is ExecutableBlueprintNodeKind.CollectionIterator)
            {
                var nativeContext = ActivatorUtilities.CreateInstance<JobContext>(
                    scope.ServiceProvider,
                    job,
                    message,
                    executableBlueprint);
                _processLifecycleTelemetry.RecordQueueWait(nativeContext, message.EnqueuedAt, receivedAt);
                await scope.ServiceProvider.GetRequiredService<CollectionIteratorExecutionService>()
                    .ExecuteAsync(nativeContext, stoppingToken);
                return;
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
                    return;
                }
            }

            blueprintNode.ResolvedTaskVersionId = runtimeVersion.Id;
            blueprintNode.ResolvedVersion = runtimeVersion.Version;

            if (!_agentManager.AgentExists(blueprintNode.ResolvedTaskVersionId))
            {
                await message.AbandonAsync(stoppingToken);
                return;
            }

            var context = ActivatorUtilities.CreateInstance<JobContext>(
                scope.ServiceProvider,
                job,
                message,
                executableBlueprint);
            _processLifecycleTelemetry.RecordQueueWait(context, message.EnqueuedAt, receivedAt);

            var started = await _jobManager.StartJob(context, stoppingToken);

            if (!started)
            {
                await message.AbandonAsync(stoppingToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process process-step message {MessageId}", message.MessageId);
            try
            {
                await message.AbandonAsync(CancellationToken.None);
            }
            catch (Exception abandonEx)
            {
                _logger.LogError(
                    abandonEx,
                    "Failed to abandon process-step message {MessageId} after processing failure.",
                    message.MessageId);
            }
        }
    }
}
