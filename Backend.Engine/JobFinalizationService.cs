using Backend.Interfaces.Queues;
using Backend.Plugins.Communication;

namespace Backend.Engine;

public class JobFinalizationService
{
    private readonly ExecutionLifecycleTelemetry _executionLifecycleTelemetry;
    private readonly ILogger<JobFinalizationService> _logger;
    private readonly IWorkflowTaskQueueWriter _workflowTaskQueueWriter;

    public JobFinalizationService(
        ExecutionLifecycleTelemetry executionLifecycleTelemetry,
        ILogger<JobFinalizationService> logger,
        IWorkflowTaskQueueWriter workflowTaskQueueWriter)
    {
        _executionLifecycleTelemetry = executionLifecycleTelemetry;
        _logger = logger;
        _workflowTaskQueueWriter = workflowTaskQueueWriter;
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

        await EnqueueNextTasksAsync(context, executionResult, cancellationToken);
        _executionLifecycleTelemetry.LogTaskCompleted(context, executionResult);

        if (await context.CompleteWorkflowIfTerminalAsync(cancellationToken))
        {
            _executionLifecycleTelemetry.LogWorkflowCompleted(
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
        _executionLifecycleTelemetry.LogTaskFailed(context, executionResult);
        _executionLifecycleTelemetry.LogWorkflowFailed(
            context,
            executionResult.CompletedAt,
            executionResult.ErrorMessage);
        await context.CompleteMessageAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(executionResult.ErrorMessage))
        {
            _logger.LogError(
                "Workflow task {BlueprintTaskId} for workflow instance {WorkflowInstanceId} failed: {ErrorMessage}",
                context.BlueprintTaskId,
                context.WorkflowInstanceId,
                executionResult.ErrorMessage);
        }
    }

    private async Task EnqueueNextTasksAsync(
        JobContext context,
        PluginExecutionResult executionResult,
        CancellationToken cancellationToken)
    {
        foreach (var outputChannel in context.WorkflowTask.OutputChannels.Values)
        {
            if (!executionResult.OutputData.ContainsKey(outputChannel.Name) ||
                outputChannel.Destination is null)
            {
                continue;
            }

            var downstreamTaskInstanceId = Guid.NewGuid();
            await context.CreatePendingTaskAsync(
                downstreamTaskInstanceId,
                outputChannel.Destination.Id,
                executionResult.CompletedAt,
                cancellationToken);

            var job = new WorkflowTaskInitialisationMessage
            {
                WorkflowInstanceId = context.WorkflowInstanceId,
                WorkflowBlueprintId = context.WorkflowBlueprintId,
                WorkflowTaskInstanceId = downstreamTaskInstanceId,
                BlueprintTaskId = outputChannel.Destination.Id,
                PreviousWorkflowTaskInstanceId = context.WorkflowTaskInstanceId,
                LinkedOutputChannelName = outputChannel.Name
            };

            await _workflowTaskQueueWriter.EnqueueAsync(job, cancellationToken);
            _logger.LogInformation(
                "Enqueued downstream task {TaskId} from output channel {OutputChannelName} for workflow instance {WorkflowInstanceId}",
                outputChannel.Destination.Id,
                outputChannel.Name,
                context.WorkflowInstanceId);
        }
    }
}
