using Backend.Database.State.Objects;
using Backend.Interfaces.Queues;
using Backend.Interfaces.Repositories;
using Backend.Models;

namespace Backend.Engine;

public sealed class StepMessageHandler
{
    private readonly IBlueprintExecutionCache _blueprintExecutionCache;
    private readonly ProcessLifecycleTelemetry _processLifecycleTelemetry;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IStepExecutionService _stepExecutionService;
    private readonly ILogger<StepMessageHandler> _logger;

    public StepMessageHandler(
        IBlueprintExecutionCache blueprintExecutionCache,
        ProcessLifecycleTelemetry processLifecycleTelemetry,
        IServiceScopeFactory serviceScopeFactory,
        IStepExecutionService stepExecutionService,
        ILogger<StepMessageHandler> logger)
    {
        _blueprintExecutionCache = blueprintExecutionCache;
        _processLifecycleTelemetry = processLifecycleTelemetry;
        _serviceScopeFactory = serviceScopeFactory;
        _stepExecutionService = stepExecutionService;
        _logger = logger;
    }

    public async Task HandleProcessStepMessageAsync(
        IProcessStepQueueMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            await HandleProcessStepMessageCoreAsync(message, cancellationToken);
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

    private async Task HandleProcessStepMessageCoreAsync(
        IProcessStepQueueMessage queueMessage,
        CancellationToken cancellationToken)
    {
        var message = queueMessage.Payload;
        var receivedAt = DateTimeOffset.UtcNow;

        await using var scope = _serviceScopeFactory.CreateAsyncScope();

        var executableBlueprint = await _blueprintExecutionCache
            .GetExecutableBlueprintAsync(message.ExecutableBlueprintId, cancellationToken);

        var blueprintNode = executableBlueprint.AllNodes
            .FirstOrDefault(node => node.Id == message.BlueprintNodeId);

        if (blueprintNode is null)
        {
            await queueMessage.AbandonAsync(cancellationToken);
            return;
        }

        var context = ActivatorUtilities.CreateInstance<StepExecutionContext>(
            scope.ServiceProvider,
            message,
            queueMessage,
            executableBlueprint);
        _processLifecycleTelemetry.RecordQueueWait(context, queueMessage.EnqueuedAt, receivedAt);

        var finalizationRepository = scope.ServiceProvider.GetRequiredService<IProcessStepFinalizationRepository>();
        var finalization = await finalizationRepository.GetAsync(
            message.ProcessStepId,
            cancellationToken);
        if (finalization is not null)
        {
            ApplyResolvedRuntimeFromFinalization(context, finalization);

            await scope.ServiceProvider.GetRequiredService<IStepFinalizationService>()
                .ResumeStepFinalizationAsync(context, finalization, cancellationToken);
            return;
        }

        if (blueprintNode.NodeKind is ExecutableBlueprintNodeKind.ConditionalBranch)
        {
            await scope.ServiceProvider.GetRequiredService<ConditionalBranchExecutionService>()
                .ExecuteAsync(context, cancellationToken);
            return;
        }

        if (blueprintNode.NodeKind is ExecutableBlueprintNodeKind.CollectionIterator)
        {
            await scope.ServiceProvider.GetRequiredService<ICollectionIteratorExecutionService>()
                .ExecuteAsync(context, cancellationToken);
            return;
        }

        var startResult = await _stepExecutionService.StartPluginStepAsync(
            context,
            cancellationToken);

        if (startResult.Disposition is StepStartDisposition.Deferred)
        {
            await queueMessage.AbandonAsync(cancellationToken);
        }
    }

    private static void ApplyResolvedRuntimeFromFinalization(
        StepExecutionContext context,
        ProcessStepFinalizationRecord finalization)
    {
        if (finalization.ResolvedTaskVersionId is not int resolvedTaskVersionId ||
            string.IsNullOrWhiteSpace(finalization.ResolvedTaskVersion) ||
            !Version.TryParse(finalization.ResolvedTaskVersion, out var resolvedVersion))
        {
            return;
        }

        context.ApplyPluginRuntime(resolvedTaskVersionId, resolvedVersion);
    }
}
