using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Interfaces.Queues;
using Backend.Interfaces.Repositories;
using Backend.Engine;
using Backend.Models;
using Backend.Plugins.Communication;
using Backend.Repositories;

namespace Backend.Maintenance;

public sealed class StepFinalizationRecoveryService
{
    private const int MaxUnrecoverableUpdateAttempts = 10;

    private readonly IProcessStepFinalizationRepository _finalizationRepository;
    private readonly IProcessStepRepository _processStepRepository;
    private readonly IProcessInstanceRepository _processInstanceRepository;
    private readonly IBlueprintExecutionCache _blueprintExecutionCache;
    private readonly IStepFinalizationService _stepFinalizationService;
    private readonly StepFinalizationRecoveryTelemetry _telemetry;
    private readonly ILogger<StepFinalizationRecoveryService> _logger;
    private readonly ILogger<StepExecutionContext> _contextLogger;

    public StepFinalizationRecoveryService(
        IProcessStepFinalizationRepository finalizationRepository,
        IProcessStepRepository processStepRepository,
        IProcessInstanceRepository processInstanceRepository,
        IBlueprintExecutionCache blueprintExecutionCache,
        IStepFinalizationService stepFinalizationService,
        StepFinalizationRecoveryTelemetry telemetry,
        ILogger<StepFinalizationRecoveryService> logger,
        ILogger<StepExecutionContext> contextLogger)
    {
        _finalizationRepository = finalizationRepository;
        _processStepRepository = processStepRepository;
        _processInstanceRepository = processInstanceRepository;
        _blueprintExecutionCache = blueprintExecutionCache;
        _stepFinalizationService = stepFinalizationService;
        _telemetry = telemetry;
        _logger = logger;
        _contextLogger = contextLogger;
    }

    public async Task<int> RecoverDueFinalizationsAsync(
        StepFinalizationRecoveryOptions options,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = await _finalizationRepository.GetRecoveryBacklogSnapshotAsync(
            now,
            options.EffectiveMinRecordAge,
            options.EffectiveMaxRetryCount,
            cancellationToken);
        _telemetry.RecordBacklog(snapshot, now);

        var records = await _finalizationRepository.GetRecoverableFinalizationsAsync(
            now,
            options.EffectiveMinRecordAge,
            options.EffectiveMaxRetryCount,
            options.EffectiveBatchSize,
            cancellationToken);

        _telemetry.RecordFound(records.Count);

        var recovered = 0;
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await RecoverOneAsync(record, cancellationToken))
                {
                    recovered++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _telemetry.RecordFailure(exception);
                _logger.LogError(
                    exception,
                    "Finalization recovery failed for process step {ProcessStepId} at stage {Stage}.",
                    record.Id,
                    record.Stage);
            }
        }

        return recovered;
    }

    private async Task<bool> RecoverOneAsync(
        ProcessStepFinalizationRecord record,
        CancellationToken cancellationToken)
    {
        if (!TryCreateMessage(record, out var message, out var validationError))
        {
            await MarkUnrecoverableAsync(record, validationError, cancellationToken);
            return false;
        }

        ProcessStepRecord step;
        try
        {
            step = await _processStepRepository.GetProcessStepById(
                message.ProcessStepId,
                message.ProcessId,
                cancellationToken);
        }
        catch (EntityNotFoundException exception)
        {
            await MarkUnrecoverableAsync(
                record,
                $"Process step {message.ProcessStepId} for process {message.ProcessId} could not be recovered: {exception.Message}",
                cancellationToken);
            return false;
        }
        catch (KeyNotFoundException exception)
        {
            await MarkUnrecoverableAsync(
                record,
                $"Process step {message.ProcessStepId} for process {message.ProcessId} could not be recovered: {exception.Message}",
                cancellationToken);
            return false;
        }

        ProcessInstanceRecord process;
        try
        {
            process = await _processInstanceRepository.GetProcessStateById(
                message.ProcessId,
                message.ExecutableBlueprintId,
                cancellationToken);
        }
        catch (EntityNotFoundException exception)
        {
            await MarkUnrecoverableAsync(
                record,
                $"Process {message.ProcessId} for blueprint version {message.ExecutableBlueprintId} could not be recovered: {exception.Message}",
                cancellationToken);
            return false;
        }
        catch (KeyNotFoundException exception)
        {
            await MarkUnrecoverableAsync(
                record,
                $"Process {message.ProcessId} for blueprint version {message.ExecutableBlueprintId} could not be recovered: {exception.Message}",
                cancellationToken);
            return false;
        }

        ExecutableBlueprint blueprint;
        try
        {
            blueprint = await _blueprintExecutionCache.GetExecutableBlueprintAsync(
                message.ExecutableBlueprintId,
                cancellationToken);
        }
        catch (EntityNotFoundException exception)
        {
            await MarkUnrecoverableAsync(
                record,
                $"Blueprint version {message.ExecutableBlueprintId} could not be recovered: {exception.Message}",
                cancellationToken);
            return false;
        }
        catch (KeyNotFoundException exception)
        {
            await MarkUnrecoverableAsync(
                record,
                $"Blueprint version {message.ExecutableBlueprintId} could not be recovered: {exception.Message}",
                cancellationToken);
            return false;
        }

        if (!blueprint.NodesById.ContainsKey(message.BlueprintNodeId))
        {
            await MarkUnrecoverableAsync(
                record,
                $"Blueprint version {message.ExecutableBlueprintId} does not contain node {message.BlueprintNodeId}.",
                cancellationToken);
            return false;
        }

        var context = new StepExecutionContext(
            message,
            _contextLogger,
            new FinalizationRecoveryProcessStepQueueMessage(message),
            blueprint);
        context.ApplyFinalizationState(step, process);

        await _stepFinalizationService.ResumeStepFinalizationForRecoveryAsync(
            context,
            record,
            cancellationToken);

        _telemetry.RecordRecovered(record.Outcome.ToString(), record.Source.ToString());
        _logger.LogInformation(
            "Recovered finalization for process step {ProcessStepId} from stage {Stage}.",
            record.Id,
            record.Stage);
        return true;
    }

    private async Task MarkUnrecoverableAsync(
        ProcessStepFinalizationRecord record,
        string message,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxUnrecoverableUpdateAttempts; attempt++)
        {
            var current = record;
            if (attempt > 0 &&
                Guid.TryParse(record.Id, out var processStepId))
            {
                current = await _finalizationRepository.GetAsync(
                    processStepId,
                    cancellationToken)
                    ?? record;
            }

            var now = DateTimeOffset.UtcNow;
            current.LastErrorKind = ProcessStepFinalizationErrorKind.FinalizationFailed;
            current.LastErrorMessage = message;
            current.LastErrorAt = now;
            current.UpdatedAt = now;

            if (await _finalizationRepository.TryUpdateAsync(current, cancellationToken))
            {
                _telemetry.RecordFailure(ProcessStepFinalizationErrorKind.FinalizationFailed.ToString());
                _logger.LogError(
                    "Marked finalization record {ProcessStepId} unrecoverable: {Message}",
                    record.Id,
                    message);
                return;
            }
        }

        throw new InvalidOperationException(
            $"Could not mark finalization record {record.Id} unrecoverable after {MaxUnrecoverableUpdateAttempts} attempts.");
    }

    private static bool TryCreateMessage(
        ProcessStepFinalizationRecord record,
        out ProcessStepInitialisationMessage message,
        out string validationError)
    {
        message = new ProcessStepInitialisationMessage();
        validationError = string.Empty;

        if (!Guid.TryParse(record.ProcessId, out var processId))
        {
            validationError = $"Finalization record {record.Id} has invalid process id '{record.ProcessId}'.";
            return false;
        }

        if (!Guid.TryParse(record.Id, out var processStepId))
        {
            validationError = $"Finalization record {record.Id} has invalid process step id.";
            return false;
        }

        if (!int.TryParse(record.BlueprintId, out var executableBlueprintId))
        {
            validationError = $"Finalization record {record.Id} has invalid blueprint version id '{record.BlueprintId}'.";
            return false;
        }

        if (!Guid.TryParse(record.BlueprintNodeId, out var blueprintNodeId))
        {
            validationError = $"Finalization record {record.Id} has invalid blueprint node id '{record.BlueprintNodeId}'.";
            return false;
        }

        message = new ProcessStepInitialisationMessage
        {
            ProcessId = processId,
            ProcessStepId = processStepId,
            ExecutableBlueprintId = executableBlueprintId,
            BlueprintNodeId = blueprintNodeId
        };
        return true;
    }

    private sealed class FinalizationRecoveryProcessStepQueueMessage : IProcessStepQueueMessage
    {
        public FinalizationRecoveryProcessStepQueueMessage(ProcessStepInitialisationMessage payload)
        {
            Payload = payload;
            EnqueuedAt = DateTimeOffset.UtcNow;
            LockedUntil = EnqueuedAt.AddMinutes(5);
            MessageId = $"finalization-recovery:{payload.ProcessStepId}";
        }

        public string MessageId { get; }

        public DateTimeOffset EnqueuedAt { get; }

        public DateTimeOffset LockedUntil { get; }

        public ProcessStepInitialisationMessage Payload { get; }

        public Task CompleteAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Finalization recovery does not have a live queue message to complete.");

        public Task AbandonAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Finalization recovery does not have a live queue message to abandon.");

        public Task<DateTimeOffset> RenewLockAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Finalization recovery does not have a live queue message lock to renew.");
    }
}
