using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Backend.Database.State;
using Backend.Engine.Messages;
using Backend.Interfaces.Repositories;
using Backend.Plugins.Communication;
using Backend.State.DAL;

namespace Backend.Engine;

public class TaskFinalizationService
{
    private readonly ILogger<TaskFinalizationService> _logger;
    private readonly IWorkflowStateRepository _workflowStateRepository;
    private readonly ServiceBusSender? _serviceBusSender;

    public TaskFinalizationService(
        ILogger<TaskFinalizationService> logger,
        IWorkflowStateRepository workflowStateRepository,
        ServiceBusSender? serviceBusSender = null)
    {
        _logger = logger;
        _workflowStateRepository = workflowStateRepository;
        _serviceBusSender = serviceBusSender;
    }

    public async Task FinalizeSuccessAsync(
        TaskContext context,
        IReadOnlyCollection<SignalAction> outputs,
        CancellationToken cancellationToken)
    {
        await PersistOutputsAsync(context, outputs, TaskExecutionResult.Success, cancellationToken);
        await context.CompleteMessageAsync(cancellationToken);
        await EnqueueNextTasksAsync(context, cancellationToken);
    }

    public async Task FinalizeFailureAsync(TaskContext context, CancellationToken cancellationToken)
    {
        await PersistOutputsAsync(context, Array.Empty<SignalAction>(), TaskExecutionResult.Failure, cancellationToken);
        await context.AbandonMessageAsync(cancellationToken);
    }

    private async Task PersistOutputsAsync(
        TaskContext context,
        IReadOnlyCollection<SignalAction> outputs,
        TaskExecutionResult result,
        CancellationToken cancellationToken)
    {
        var state = await context.GetWorkflowStateAsync();
        var taskState = state.Tasks.FirstOrDefault(t => t.WorkflowTaskInstanceId == context.WorkflowTaskInstanceId);

        if (taskState is null)
        {
            _logger.LogWarning(
                "Unable to locate task state for workflow instance {WorkflowInstanceId} task {TaskInstanceId}",
                context.WorkflowInstanceId,
                context.WorkflowTaskInstanceId);
            return;
        }

        foreach (var action in outputs.Where(a => a.Class == SignalActionClass.OutputChannel))
        {
            taskState.OutputChannelStateDataStreams[action.Identifier] = new MemoryStream(action.Data, writable: false);
        }

        taskState.CompletedAt = DateTimeOffset.UtcNow;
        taskState.Result = result;
        state.LastUpdatedAt = DateTimeOffset.UtcNow;

        await _workflowStateRepository.UpdateWorkflowState(state);
        _logger.LogInformation(
            "Persisted {OutputCount} outputs for workflow instance {WorkflowInstanceId} task {TaskInstanceId}",
            outputs.Count,
            context.WorkflowInstanceId,
            context.WorkflowTaskInstanceId);
    }

    private async Task EnqueueNextTasksAsync(TaskContext context, CancellationToken cancellationToken)
    {
        if (_serviceBusSender is null)
        {
            _logger.LogWarning("No Service Bus sender configured; skipping enqueue of downstream tasks.");
            return;
        }

        foreach (var nextTask in context.NextTasks)
        {
            var job = new WorkflowTaskExecutionJob
            {
                WorkflowInstanceId = context.WorkflowInstanceId,
                WorkflowBlueprintId = context.WorkflowBlueprintId,
                WorkflowTaskInstanceId = Guid.NewGuid(),
                WorkflowTaskId = nextTask.Id
            };

            var message = new ServiceBusMessage(BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(job)))
            {
                ContentType = "application/json"
            };

            await _serviceBusSender.SendMessageAsync(message, cancellationToken);
            _logger.LogInformation(
                "Enqueued downstream task {TaskId} for workflow instance {WorkflowInstanceId}",
                nextTask.Id,
                context.WorkflowInstanceId);
        }
    }
}
