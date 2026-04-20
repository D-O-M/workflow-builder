using Backend.Interfaces.Queues;
using Backend.Plugins.Communication;

namespace Backend.Engine;

public class JobFinalizationService
{
    private readonly ProcessLifecycleTelemetry _processLifecycleTelemetry;
    private readonly ILogger<JobFinalizationService> _logger;
    private readonly IProcessStepQueueWriter _processStepQueueWriter;

    public JobFinalizationService(
        ProcessLifecycleTelemetry processLifecycleTelemetry,
        ILogger<JobFinalizationService> logger,
        IProcessStepQueueWriter processStepQueueWriter)
    {
        _processLifecycleTelemetry = processLifecycleTelemetry;
        _logger = logger;
        _processStepQueueWriter = processStepQueueWriter;
    }

    public async Task FinalizeSuccess(
        JobContext context,
        PluginExecutionResult executionResult,
        CancellationToken cancellationToken)
    {
        await context.PersistSuccessAsync(
            executionResult.OutputData,
            executionResult.GlobalState,
            executionResult.CompletedAt,
            cancellationToken);

        await EnqueueNextStepsAsync(context, executionResult, cancellationToken);
        _processLifecycleTelemetry.LogStepCompleted(context, executionResult);

        if (await context.CompleteProcessIfTerminalAsync(cancellationToken))
        {
            _processLifecycleTelemetry.LogProcessCompleted(
                context,
                executionResult.CompletedAt);
        }

        await context.CompleteMessageAsync(cancellationToken);
    }

    public async Task FinalizeFailureAsync(
        JobContext context,
        PluginExecutionResult executionResult,
        CancellationToken cancellationToken)
    {
        await context.PersistFailureAsync(
            executionResult.GlobalState,
            executionResult.ErrorMessage,
            executionResult.CompletedAt,
            cancellationToken);
        _processLifecycleTelemetry.LogStepFailed(context, executionResult);
        _processLifecycleTelemetry.LogProcessFailed(
            context,
            executionResult.CompletedAt,
            executionResult.ErrorMessage);
        await context.CompleteMessageAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(executionResult.ErrorMessage))
        {
            _logger.LogError(
                "Process step {BlueprintNodeId} for process {ProcessId} failed: {ErrorMessage}",
                context.BlueprintNodeId,
                context.ProcessId,
                executionResult.ErrorMessage);
        }
    }

    private async Task EnqueueNextStepsAsync(
        JobContext context,
        PluginExecutionResult executionResult,
        CancellationToken cancellationToken)
    {
        foreach (var outputChannel in context.BlueprintNode.OutputChannels.Values)
        {
            if (!executionResult.OutputData.ContainsKey(outputChannel.Name) ||
                outputChannel.Destination is null)
            {
                continue;
            }

            var downstreamProcessStepId = Guid.NewGuid();
            await context.CreatePendingStepAsync(
                downstreamProcessStepId,
                outputChannel.Destination.Id,
                executionResult.CompletedAt,
                cancellationToken);

            var job = new ProcessStepInitialisationMessage
            {
                ProcessId = context.ProcessId,
                ExecutableBlueprintId = context.ExecutableBlueprintId,
                ProcessStepId = downstreamProcessStepId,
                BlueprintNodeId = outputChannel.Destination.Id,
                PreviousProcessStepId = context.ProcessStepId,
                LinkedOutputChannelName = outputChannel.Name
            };

            await _processStepQueueWriter.EnqueueAsync(job, cancellationToken);
            _logger.LogInformation(
                "Enqueued downstream step {BlueprintNodeId} from output channel {OutputChannelName} for process {ProcessId}",
                outputChannel.Destination.Id,
                outputChannel.Name,
                context.ProcessId);
        }
    }
}
