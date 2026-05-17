using System.Diagnostics;
using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Interfaces.Queues;
using Backend.Models;
using Backend.Plugins.Communication;

namespace Backend.Engine;

public class StepExecutionContext
{
    private readonly CancellationTokenSource _cts = new();
    private readonly ProcessStepInitialisationMessage _message;
    private readonly ILogger _logger;
    private readonly IProcessStepQueueMessage _queueMessage;
    private readonly ExecutableBlueprint _executableBlueprint;
    private readonly Lazy<ExecutableBlueprintNode> _blueprintNode;

    public StepExecutionContext(
        ProcessStepInitialisationMessage message,
        ILogger<StepExecutionContext> logger,
        IProcessStepQueueMessage queueMessage,
        ExecutableBlueprint executableBlueprint)
    {
        _message = message;
        _logger = logger;
        _queueMessage = queueMessage;
        _executableBlueprint = executableBlueprint;
        _blueprintNode = new Lazy<ExecutableBlueprintNode>(
            () => _executableBlueprint.NodesById[message.BlueprintNodeId]);

        Id = message.ProcessStepId;
    }

    public Guid Id { get; }
    public ProcessStepInitialisationMessage Message => _message;
    public ExecutableBlueprint ExecutableBlueprint => _executableBlueprint;
    public Guid ProcessId => _message.ProcessId;
    public Guid ProcessStepId => _message.ProcessStepId;
    public int ExecutableBlueprintId => _message.ExecutableBlueprintId;
    public int BlueprintDefinitionId => _executableBlueprint.DefinitionId;
    public int BlueprintVersionNumber => _executableBlueprint.VersionNumber;
    public string BlueprintName => _executableBlueprint.Name;
    public Guid BlueprintNodeId => _message.BlueprintNodeId;
    public int ResolvedTaskVersionId => BlueprintNode.ResolvedTaskVersionId;
    public int TaskDefinitionId => BlueprintNode.TaskDefinitionId;
    public int AllowedMajor => BlueprintNode.AllowedMajor;
    public int AllowedMinor => BlueprintNode.AllowedMinor;
    public string TaskName => BlueprintNode.Name;
    public string PluginName => BlueprintNode.Name;
    public string InputChannelName => BlueprintNode.InputChannel.Name;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? PluginQueuedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public InputHydrationPlan? InputHydrationPlan { get; private set; }
    public ProcessStepIterationContext? IterationContext { get; private set; }
    public IReadOnlyDictionary<string, Dictionary<string, JsonElement>> ProcessNodeOverrides { get; private set; }
        = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal);
    public string[] OutputChannelsPopulated { get; set; } = [];
    public ExecutableBlueprintNode BlueprintNode => _blueprintNode.Value;
    public ProcessPhaseMetadata? PhaseMetadata { get; private set; }
    public Activity? ExecutionActivity { get; set; }
    public DateTimeOffset ProcessingWindowExpiresAt => _queueMessage.LockedUntil;

    public InvocationEnvelope ToInvocationEnvelope(InvocationTelemetryContext? telemetryContext = null) =>
        new(
            ExecutableBlueprintId,
            BlueprintDefinitionId,
            ProcessId,
            ProcessStepId,
            BlueprintNodeId,
            TaskDefinitionId,
            ResolvedTaskVersionId,
            BlueprintNode.ResolvedVersion.ToString(),
            PluginName,
            InputHydrationPlan ?? throw new InvalidOperationException(
                $"Process step {ProcessStepId} has no input hydration plan."))
        {
            TelemetryContext = telemetryContext
        };

    public void ApplyPluginRuntime(PluginRuntimeResolution resolution)
    {
        BlueprintNode.ResolvedTaskVersionId = resolution.TaskVersionId;
        BlueprintNode.ResolvedVersion = resolution.Version;
    }

    public void ApplyPluginRuntime(int resolvedTaskVersionId, Version resolvedVersion)
    {
        BlueprintNode.ResolvedTaskVersionId = resolvedTaskVersionId;
        BlueprintNode.ResolvedVersion = resolvedVersion;
    }

    public void ApplyClaimedStep(
        ProcessStepRecord step,
        ProcessInstanceRecord<Dictionary<string, JsonElement>> process)
    {
        ApplyStep(step);
        ApplyProcess(process);
    }

    public void ApplyFinalizationState(
        ProcessStepRecord step,
        ProcessInstanceRecord<Dictionary<string, JsonElement>> process)
    {
        ApplyStep(step);
        ApplyProcess(process);
    }

    public void ApplyProcess(ProcessInstanceRecord<Dictionary<string, JsonElement>> process)
    {
        ProcessNodeOverrides = process.NodeOverrides;
        PhaseMetadata = process.Phase;
    }

    public async Task<bool> ExtendProcessingWindow()
    {
        try
        {
            _logger.LogInformation(
                "Renewing lock on message {MessageId} for step {BlueprintNodeId} of process {ProcessId} of blueprint version {ExecutableBlueprintId}",
                _queueMessage.MessageId,
                _message.BlueprintNodeId,
                _message.ProcessId,
                _message.ExecutableBlueprintId);

            await _queueMessage.RenewLockAsync(_cts.Token);

            _logger.LogInformation(
                "Successfully renewed lock on message {MessageId} for step {BlueprintNodeId} of process {ProcessId} of blueprint version {ExecutableBlueprintId}",
                _queueMessage.MessageId,
                _message.BlueprintNodeId,
                _message.ProcessId,
                _message.ExecutableBlueprintId);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to renew lock on message {MessageId} for step {BlueprintNodeId} of process {ProcessId} of blueprint version {ExecutableBlueprintId}",
                _queueMessage.MessageId,
                _message.BlueprintNodeId,
                _message.ProcessId,
                _message.ExecutableBlueprintId);

            return false;
        }
    }

    public Task CompleteMessageAsync(CancellationToken cancellationToken) =>
        _queueMessage.CompleteAsync(cancellationToken);

    public Task AbandonMessageAsync(CancellationToken cancellationToken) =>
        _queueMessage.AbandonAsync(cancellationToken);

    private void ApplyStep(ProcessStepRecord step)
    {
        InputHydrationPlan = step.InputHydrationPlan;
        StartedAt = step.StartedAt;
        ClaimedAt = step.StartedAt;
        CompletedAt = step.CompletedAt ?? step.FailedAt;
        IterationContext = step.IterationContext;

        if (step.ResolvedTaskVersionId is int resolvedTaskVersionId)
        {
            BlueprintNode.ResolvedTaskVersionId = resolvedTaskVersionId;
        }

        if (!string.IsNullOrWhiteSpace(step.ResolvedTaskVersion) &&
            Version.TryParse(step.ResolvedTaskVersion, out var resolvedVersion))
        {
            BlueprintNode.ResolvedVersion = resolvedVersion;
        }
    }
}
