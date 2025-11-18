using Azure.Messaging.ServiceBus;
using Backend.DTOs;
using Backend.Engine.Messages;
using Backend.Interfaces.Repositories;
using Backend.State.DAL;

namespace Backend.Engine;

public class TaskContext
{
    private readonly CancellationTokenSource _cts = new();
    private readonly int _id;
    private readonly WorkflowTaskExecutionJob _job;
    private readonly ILogger _logger;
    private readonly ServiceBusReceivedMessage _message;
    private readonly ServiceBusReceiver _serviceBusReceiver;
    private readonly IWorkflowStateRepository _workflowStateRepository;
    private readonly WorkflowBlueprint _workflowBlueprint;
    private readonly Lazy<WorkflowTask> _workflowTask;
    private WorkflowInstanceState? _workflowState;

    public Guid Guid { get; private set; }
    public int SchemaId => _workflowTask.Value.SchemaId;
    public Task<Stream> InputChannelDataStream => GetInputChannelDataStream();

    public WorkflowTask[] NextTasks => _workflowTask.Value
        .OutputChannels.Values.Select(v => v.Destination)
        .Where(d => d is not null)
        .ToArray()!;

    public DateTimeOffset ProcessingWindowExpiresAt => _message.LockedUntil;

    public TaskContext(
        WorkflowTaskExecutionJob job,
        ILogger<TaskContext> logger,
        ServiceBusReceivedMessage message,
        ServiceBusReceiver serviceBusReceiver,
        IWorkflowStateRepository workflowStateRepository,
        WorkflowBlueprint workflowBlueprint)
    {
        _id = job.WorkflowTaskId;
        _job = job;
        _logger = logger;
        _message = message;
        _serviceBusReceiver = serviceBusReceiver;
        _workflowStateRepository = workflowStateRepository;
        _workflowBlueprint = workflowBlueprint;
        _workflowTask = new Lazy<WorkflowTask>(
            () => _workflowBlueprint.AllTasks.First(t => t.Id == _id));
    }

    public async Task<bool> ExtendProcessingWindow()
    {
        try
        {
            _logger.LogInformation("Renewing lock on message {MessageId} for task " +
                "{WorkflowTaskId} of Workflow Instance {WorkflowInstanceId} of " +
                "Workflow Blueprint {WorkflowBlueprintId}", _message.MessageId,
                _job.WorkflowTaskId, _job.WorkflowInstanceId, _job.WorkflowBlueprintId);

            await _serviceBusReceiver.RenewMessageLockAsync(_message, _cts.Token);

            _logger.LogInformation("Successfully renewed lock on message {MessageId} for task " +
                "{WorkflowTaskId} of Workflow Instance {WorkflowInstanceId} of " +
                "Workflow Blueprint {WorkflowBlueprintId}", _message.MessageId,
                _job.WorkflowTaskId, _job.WorkflowInstanceId, _job.WorkflowBlueprintId);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to renew lock on message {MessageId} for task " +
                "{WorkflowTaskId} of Workflow Instance {WorkflowInstanceId} of " +
                "Workflow Blueprint {WorkflowBlueprintId}", _message.MessageId,
                _job.WorkflowTaskId, _job.WorkflowInstanceId, _job.WorkflowBlueprintId);

            return false;
        }
    }

    private async Task<Stream> GetInputChannelDataStream()
    {
        _workflowState ??= await _workflowStateRepository.GetWorkflowStateById(_job.WorkflowInstanceId);
        var taskState = _workflowState.Tasks.FirstOrDefault(t => t.WorkflowTaskInstanceId == _job.WorkflowTaskInstanceId);

        if (taskState is null)
            throw new Exception();

        return taskState.InputChannelStateDataStream;
    }
}
