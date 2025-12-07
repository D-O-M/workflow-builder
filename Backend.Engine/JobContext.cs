using Azure.Messaging.ServiceBus;
using Backend.DTOs;
using Backend.Engine.Messages;

namespace Backend.Engine;

public class JobContext
{
    private readonly CancellationTokenSource _cts = new();
    private readonly int _id;
    private readonly WorkflowTaskInitialisationMessage _message;
    private readonly ILogger _logger;
    private readonly ServiceBusReceivedMessage _messageContainer;
    private readonly ServiceBusReceiver _serviceBusReceiver;
    private readonly WorkflowBlueprint _workflowBlueprint;
    private readonly Lazy<WorkflowTask> _workflowTask;

    public Guid Guid { get; private set; }
    public WorkflowTaskInitialisationMessage Job => _message;
    public WorkflowBlueprint WorkflowBlueprint => _workflowBlueprint;
    public Guid WorkflowInstanceId => _message.WorkflowInstanceId;
    public Guid WorkflowTaskInstanceId => _message.WorkflowTaskInstanceId;
    public int WorkflowBlueprintId => _message.WorkflowBlueprintId;
    public int SchemaId => _workflowTask.Value.SchemaId;
    public string InputChannelName => _workflowTask.Value.InputChannel.Name;
    public Guid PreviousTaskInstanceId => _message.PreviousWorkflowTaskInstanceId;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string[] OutputChannelsPopulated { get; set; } = [];

    public WorkflowTask[] NextTasks => _workflowTask.Value
        .OutputChannels.Values.Select(v => v.Destination)
        .Where(d => d is not null)
        .ToArray()!;

    public DateTimeOffset ProcessingWindowExpiresAt => _messageContainer.LockedUntil;

    public JobContext(
        WorkflowTaskInitialisationMessage job,
        ILogger<JobContext> logger,
        ServiceBusReceivedMessage message,
        ServiceBusReceiver serviceBusReceiver,
        WorkflowBlueprint workflowBlueprint)
    {
        _id = job.WorkflowTaskId;
        _message = job;
        _logger = logger;
        _messageContainer = message;
        _serviceBusReceiver = serviceBusReceiver;
        _workflowBlueprint = workflowBlueprint;
        _workflowTask = new Lazy<WorkflowTask>(
            () => _workflowBlueprint.AllTasks.First(t => t.Id == _id));

        Guid = job.WorkflowTaskInstanceId;
    }

    public async Task<bool> ExtendProcessingWindow()
    {
        try
        {
            _logger.LogInformation("Renewing lock on message {MessageId} for task " +
                "{WorkflowTaskId} of Workflow Instance {WorkflowInstanceId} of " +
                "Workflow Blueprint {WorkflowBlueprintId}", _messageContainer.MessageId,
                _message.WorkflowTaskId, _message.WorkflowInstanceId, _message.WorkflowBlueprintId);

            await _serviceBusReceiver.RenewMessageLockAsync(_messageContainer, _cts.Token);

            _logger.LogInformation("Successfully renewed lock on message {MessageId} for task " +
                "{WorkflowTaskId} of Workflow Instance {WorkflowInstanceId} of " +
                "Workflow Blueprint {WorkflowBlueprintId}", _messageContainer.MessageId,
                _message.WorkflowTaskId, _message.WorkflowInstanceId, _message.WorkflowBlueprintId);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to renew lock on message {MessageId} for task " +
                "{WorkflowTaskId} of Workflow Instance {WorkflowInstanceId} of " +
                "Workflow Blueprint {WorkflowBlueprintId}", _messageContainer.MessageId,
                _message.WorkflowTaskId, _message.WorkflowInstanceId, _message.WorkflowBlueprintId);

            return false;
        }
    }

    public async Task CompleteMessageAsync(CancellationToken cancellationToken)
    {
        await _serviceBusReceiver.CompleteMessageAsync(_messageContainer, cancellationToken);
    }

    public async Task AbandonMessageAsync(CancellationToken cancellationToken)
    {
        await _serviceBusReceiver.AbandonMessageAsync(_messageContainer, cancellationToken: cancellationToken);
    }
}
