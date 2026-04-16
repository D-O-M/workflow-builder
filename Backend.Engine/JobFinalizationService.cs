using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Backend.Plugins.Communication;

namespace Backend.Engine;

public class JobFinalizationService
{
    private readonly ILogger<JobFinalizationService> _logger;
    private readonly ServiceBusSender _serviceBusSender;

    public JobFinalizationService(
        ILogger<JobFinalizationService> logger,
        ServiceBusSender serviceBusSender)
    {
        _logger = logger;
        _serviceBusSender = serviceBusSender;
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
        await context.CompleteMessageAsync(cancellationToken);
    }

    public async Task FinalizeFailureAsync(
        JobContext context,
        PluginExecutionResult executionResult,
        CancellationToken cancellationToken)
    {
        await context.PersistFailureAsync(
            executionResult.GlobalState,
            executionResult.CompletedAt,
            cancellationToken);
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

            var message = new ServiceBusMessage(BinaryData.FromBytes(
                JsonSerializer.SerializeToUtf8Bytes(job)))
            {
                ContentType = "application/json"
            };

            await _serviceBusSender.SendMessageAsync(message, cancellationToken);
            _logger.LogInformation(
                "Enqueued downstream task {TaskId} from output channel {OutputChannelName} for workflow instance {WorkflowInstanceId}",
                outputChannel.Destination.Id,
                outputChannel.Name,
                context.WorkflowInstanceId);
        }
    }
}
