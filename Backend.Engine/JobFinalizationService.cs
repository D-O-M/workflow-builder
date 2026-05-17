using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Models;
using Backend.Plugins.Communication;
using Backend.Repositories;

namespace Backend.Engine;

public sealed record CollectionIteratorFinalizationResult(
    int ItemCount,
    int QueuedStepCount,
    TimeSpan DownstreamActivationElapsed);

public class JobFinalizationService
{
    private const int MaxFinalizationUpdateAttempts = 10;
    private const string CollectionIteratorOutputChannelName = "Item";
    private const string CollectionInputPropertyName = "items";

    private static readonly JsonSerializerOptions NormalizedJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly NodeActivationService _nodeActivationService;
    private readonly PhasedProcessFinalizationService _phasedProcessFinalizationService;
    private readonly IProcessStepFinalizationRepository _finalizationRepository;
    private readonly ProcessLifecycleTelemetry _processLifecycleTelemetry;
    private readonly ILogger<JobFinalizationService> _logger;

    public JobFinalizationService(
        NodeActivationService nodeActivationService,
        PhasedProcessFinalizationService phasedProcessFinalizationService,
        IProcessStepFinalizationRepository finalizationRepository,
        ProcessLifecycleTelemetry processLifecycleTelemetry,
        ILogger<JobFinalizationService> logger)
    {
        _nodeActivationService = nodeActivationService;
        _phasedProcessFinalizationService = phasedProcessFinalizationService;
        _finalizationRepository = finalizationRepository;
        _processLifecycleTelemetry = processLifecycleTelemetry;
        _logger = logger;
    }

    public async Task FinalizeSuccess(
        JobContext context,
        PluginExecutionResult executionResult,
        CancellationToken cancellationToken)
    {
        var record = await CreateOrGetFinalizationRecordAsync(
            context,
            executionResult,
            ProcessStepFinalizationSource.Plugin,
            collectionIteratorItemCount: null,
            collectionIteratorRepeatedBlueprintNodeCount: null,
            collectionIteratorBatchSize: null,
            cancellationToken);

        await RunWithRetryableErrorCheckpointAsync(
            record.Id,
            () => ContinueSuccessFinalizationAsync(context, record, cancellationToken),
            cancellationToken);
    }

    public async Task FinalizeFailureAsync(
        JobContext context,
        PluginExecutionResult executionResult,
        CancellationToken cancellationToken)
    {
        var record = await CreateOrGetFinalizationRecordAsync(
            context,
            executionResult,
            ProcessStepFinalizationSource.Plugin,
            collectionIteratorItemCount: null,
            collectionIteratorRepeatedBlueprintNodeCount: null,
            collectionIteratorBatchSize: null,
            cancellationToken);

        await RunWithRetryableErrorCheckpointAsync(
            record.Id,
            () => ContinueFailureFinalizationAsync(context, record, cancellationToken),
            cancellationToken);
    }

    public async Task<CollectionIteratorFinalizationResult> FinalizeCollectionIteratorSuccessAsync(
        JobContext context,
        PluginExecutionResult executionResult,
        int repeatedBlueprintNodeCount,
        int itemCount,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var record = await CreateOrGetFinalizationRecordAsync(
            context,
            executionResult,
            ProcessStepFinalizationSource.CollectionIterator,
            itemCount,
            repeatedBlueprintNodeCount,
            batchSize,
            cancellationToken);

        return await RunWithRetryableErrorCheckpointAsync(
            record.Id,
            () => ContinueSuccessFinalizationAsync(context, record, cancellationToken),
            cancellationToken);
    }

    public async Task FinalizeCollectionIteratorFailureAsync(
        JobContext context,
        PluginExecutionResult executionResult,
        CancellationToken cancellationToken)
    {
        var record = await CreateOrGetFinalizationRecordAsync(
            context,
            executionResult,
            ProcessStepFinalizationSource.CollectionIterator,
            collectionIteratorItemCount: null,
            collectionIteratorRepeatedBlueprintNodeCount: null,
            collectionIteratorBatchSize: null,
            cancellationToken);

        await RunWithRetryableErrorCheckpointAsync(
            record.Id,
            () => ContinueFailureFinalizationAsync(context, record, cancellationToken),
            cancellationToken);
    }

    public async Task<CollectionIteratorFinalizationResult> ResumeFinalizationAsync(
        JobContext context,
        ProcessStepFinalizationRecord record,
        CancellationToken cancellationToken)
    {
        await context.HydrateForFinalizationAsync(cancellationToken);

        if (record.Stage is ProcessStepFinalizationStage.QueueMessageCompleted)
        {
            await context.CompleteMessageAsync(cancellationToken);
            return new CollectionIteratorFinalizationResult(
                record.CollectionIteratorItemCount ?? 0,
                QueuedStepCount: 0,
                DownstreamActivationElapsed: TimeSpan.Zero);
        }

        return record.Outcome switch
        {
            ProcessStepFinalizationOutcome.Succeeded => await RunWithRetryableErrorCheckpointAsync(
                record.Id,
                () => ContinueSuccessFinalizationAsync(context, record, cancellationToken),
                cancellationToken),
            ProcessStepFinalizationOutcome.Failed => await RunWithRetryableErrorCheckpointAsync(
                record.Id,
                () => ContinueFailureFinalizationAsync(context, record, cancellationToken),
                cancellationToken),
            _ => throw new InvalidOperationException(
                $"Unsupported finalization outcome '{record.Outcome}' for step {context.ProcessStepId}.")
        };
    }

    private async Task<CollectionIteratorFinalizationResult> ContinueSuccessFinalizationAsync(
        JobContext context,
        ProcessStepFinalizationRecord initialRecord,
        CancellationToken cancellationToken)
    {
        var record = await _finalizationRepository.GetAsync(
                Guid.Parse(initialRecord.Id),
                cancellationToken)
            ?? initialRecord;

        if (!HasReached(record, ProcessStepFinalizationStage.OutputsPersisted))
        {
            await PersistSuccessOutputsAsync(context, record, cancellationToken);
            record = await AdvanceStageAsync(
                record.Id,
                ProcessStepFinalizationStage.OutputsPersisted,
                trimResultSnapshot: true,
                cancellationToken);
        }

        var activationResult = await BuildSuccessResultAsync(context, record, cancellationToken);
        CollectionIteratorFinalizationResult collectionResult = new(
            record.CollectionIteratorItemCount ?? 0,
            QueuedStepCount: 0,
            DownstreamActivationElapsed: TimeSpan.Zero);

        if (!HasReached(record, ProcessStepFinalizationStage.DownstreamActivationCompleted))
        {
            if (record.Source is ProcessStepFinalizationSource.CollectionIterator)
            {
                collectionResult = await EnqueueCollectionIteratorDownstreamStepsAsync(
                    context,
                    record,
                    cancellationToken);
            }
            else
            {
                await _nodeActivationService.EnqueueReadyDownstreamStepsAsync(
                    context,
                    activationResult,
                    cancellationToken);
            }

            record = await AdvanceStageAsync(
                record.Id,
                ProcessStepFinalizationStage.DownstreamActivationCompleted,
                trimResultSnapshot: false,
                cancellationToken);
        }

        _processLifecycleTelemetry.LogStepCompleted(context, activationResult);

        if (!HasReached(record, ProcessStepFinalizationStage.ProcessFinalizationCompleted))
        {
            if (await context.IsProcessCompletedAsync(cancellationToken))
            {
                if (context.PhaseMetadata is not null)
                {
                    await _phasedProcessFinalizationService.FinalizeCompletedPhaseAsync(
                        context.PhaseMetadata,
                        context.ExecutableBlueprintId,
                        context.ProcessId,
                        record.CompletedAt,
                        cancellationToken);
                }

                _processLifecycleTelemetry.LogProcessCompleted(
                    context,
                    record.CompletedAt);
            }

            record = await AdvanceStageAsync(
                record.Id,
                ProcessStepFinalizationStage.ProcessFinalizationCompleted,
                trimResultSnapshot: false,
                cancellationToken);
        }

        await CompleteDurableFinalizationAndMessageAsync(context, record, cancellationToken);
        return collectionResult;
    }

    private async Task<CollectionIteratorFinalizationResult> ContinueFailureFinalizationAsync(
        JobContext context,
        ProcessStepFinalizationRecord initialRecord,
        CancellationToken cancellationToken)
    {
        var record = await _finalizationRepository.GetAsync(
                Guid.Parse(initialRecord.Id),
                cancellationToken)
            ?? initialRecord;

        if (!HasReached(record, ProcessStepFinalizationStage.FailurePersisted))
        {
            await context.PersistFailureAsync(
                record.EffectiveInputData,
                record.GlobalState,
                record.ErrorMessage,
                record.CompletedAt,
                cancellationToken);

            record = await AdvanceStageAsync(
                record.Id,
                ProcessStepFinalizationStage.FailurePersisted,
                trimResultSnapshot: true,
                cancellationToken);
        }

        var result = BuildFailureResult(context, record);

        if (!HasReached(record, ProcessStepFinalizationStage.ProcessFinalizationCompleted))
        {
            if (context.PhaseMetadata is not null)
            {
                await _phasedProcessFinalizationService.MarkPhaseFailedAsync(
                    context.PhaseMetadata,
                    context.ExecutableBlueprintId,
                    context.ProcessId,
                    record.ErrorMessage,
                    record.CompletedAt,
                    cancellationToken);
            }

            _processLifecycleTelemetry.LogStepFailed(context, result);
            _processLifecycleTelemetry.LogProcessFailed(
                context,
                record.CompletedAt,
                record.ErrorMessage);

            record = await AdvanceStageAsync(
                record.Id,
                ProcessStepFinalizationStage.ProcessFinalizationCompleted,
                trimResultSnapshot: false,
                cancellationToken);
        }

        await CompleteDurableFinalizationAndMessageAsync(context, record, cancellationToken);

        if (!string.IsNullOrWhiteSpace(record.ErrorMessage))
        {
            _logger.LogError(
                "Process step {BlueprintNodeId} for process {ProcessId} failed: {ErrorMessage}",
                context.BlueprintNodeId,
                context.ProcessId,
                record.ErrorMessage);
        }

        return new CollectionIteratorFinalizationResult(
            record.CollectionIteratorItemCount ?? 0,
            QueuedStepCount: 0,
            DownstreamActivationElapsed: TimeSpan.Zero);
    }

    private async Task PersistSuccessOutputsAsync(
        JobContext context,
        ProcessStepFinalizationRecord record,
        CancellationToken cancellationToken)
    {
        var outputData = record.OutputData
            ?? throw new InvalidOperationException(
                $"Finalization record {record.Id} has no output data to persist.");

        await context.PersistSuccessAsync(
            record.EffectiveInputData,
            outputData,
            record.GlobalState,
            record.CompletedAt,
            cancellationToken);

        if (record.Source is ProcessStepFinalizationSource.CollectionIterator)
        {
            await context.ApplyCollectionIteratorExpansionAsync(
                record.CollectionIteratorRepeatedBlueprintNodeCount ?? 0,
                record.CollectionIteratorItemCount ?? 0,
                record.CompletedAt,
                cancellationToken);
        }
    }

    private async Task<CollectionIteratorFinalizationResult> EnqueueCollectionIteratorDownstreamStepsAsync(
        JobContext context,
        ProcessStepFinalizationRecord record,
        CancellationToken cancellationToken)
    {
        var items = await ResolveCollectionIteratorItemsAsync(context, record, cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        var queuedStepCount = await _nodeActivationService.EnqueueCollectionIteratorItemStepsAsync(
            context,
            items,
            Math.Max(1, record.CollectionIteratorBatchSize ?? 1),
            record.CompletedAt,
            cancellationToken);
        stopwatch.Stop();

        context.OutputChannelsPopulated = items.Count > 0 ? [CollectionIteratorOutputChannelName] : [];
        return new CollectionIteratorFinalizationResult(
            items.Count,
            queuedStepCount,
            stopwatch.Elapsed);
    }

    private async Task<IReadOnlyList<JsonElement>> ResolveCollectionIteratorItemsAsync(
        JobContext context,
        ProcessStepFinalizationRecord record,
        CancellationToken cancellationToken)
    {
        var effectiveInputData = record.EffectiveInputData;
        if (effectiveInputData is null)
        {
            var step = await context.GetPersistedStepAsync(cancellationToken);
            effectiveInputData = step.InputData;
        }

        if (effectiveInputData is null ||
            !effectiveInputData.TryGetValue(context.InputChannelName, out var channelPayload) ||
            channelPayload.ValueKind != JsonValueKind.Object ||
            !channelPayload.TryGetProperty(CollectionInputPropertyName, out var itemsValue) ||
            itemsValue.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Collection Iterator finalization for step {context.ProcessStepId} cannot resume because persisted input does not contain an '{CollectionInputPropertyName}' array.");
        }

        return itemsValue.EnumerateArray()
            .Select(item => item.Clone())
            .ToList();
    }

    private async Task<PluginExecutionResult> BuildSuccessResultAsync(
        JobContext context,
        ProcessStepFinalizationRecord record,
        CancellationToken cancellationToken)
    {
        var outputData = record.OutputData;
        if (outputData is null)
        {
            var step = await context.GetPersistedStepAsync(cancellationToken);
            outputData = step.OutputData;
        }

        if (outputData is null)
        {
            throw new InvalidOperationException(
                $"Finalization record {record.Id} reached {record.Stage} but no persisted output data was found.");
        }

        return new PluginExecutionResult(
            context.ProcessStepId,
            Succeeded: true,
            EffectiveInputData: record.EffectiveInputData,
            OutputData: CloneJsonMap(outputData),
            GlobalState: record.GlobalState,
            StartedAt: record.StartedAt,
            CompletedAt: record.CompletedAt,
            TimingsMs: record.TimingsMs,
            ErrorMessage: null);
    }

    private static PluginExecutionResult BuildFailureResult(
        JobContext context,
        ProcessStepFinalizationRecord record) =>
        new(
            context.ProcessStepId,
            Succeeded: false,
            EffectiveInputData: record.EffectiveInputData,
            OutputData: new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            GlobalState: record.GlobalState,
            StartedAt: record.StartedAt,
            CompletedAt: record.CompletedAt,
            TimingsMs: record.TimingsMs,
            ErrorMessage: record.ErrorMessage);

    private async Task CompleteDurableFinalizationAndMessageAsync(
        JobContext context,
        ProcessStepFinalizationRecord record,
        CancellationToken cancellationToken)
    {
        await AdvanceStageAsync(
            record.Id,
            ProcessStepFinalizationStage.QueueMessageCompleted,
            trimResultSnapshot: false,
            cancellationToken);
        await context.CompleteMessageAsync(cancellationToken);
    }

    private async Task<ProcessStepFinalizationRecord> CreateOrGetFinalizationRecordAsync(
        JobContext context,
        PluginExecutionResult result,
        ProcessStepFinalizationSource source,
        int? collectionIteratorItemCount,
        int? collectionIteratorRepeatedBlueprintNodeCount,
        int? collectionIteratorBatchSize,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var record = new ProcessStepFinalizationRecord
        {
            id = context.ProcessStepId.ToString(),
            ProcessId = context.ProcessId.ToString(),
            BlueprintId = context.ExecutableBlueprintId.ToString(),
            BlueprintNodeId = context.BlueprintNodeId.ToString(),
            TaskDefinitionId = context.TaskDefinitionId,
            ResolvedTaskVersionId = context.ResolvedTaskVersionId,
            ResolvedTaskVersion = context.BlueprintNode.ResolvedVersion.ToString(),
            Outcome = result.Succeeded
                ? ProcessStepFinalizationOutcome.Succeeded
                : ProcessStepFinalizationOutcome.Failed,
            Source = source,
            Stage = ProcessStepFinalizationStage.ResultReceived,
            EffectiveInputData = NormalizeJsonMap(result.EffectiveInputData),
            OutputData = NormalizeJsonMap(result.OutputData),
            GlobalState = NormalizeJsonMap(result.GlobalState),
            TimingsMs = result.TimingsMs is null
                ? null
                : new Dictionary<string, double>(result.TimingsMs, StringComparer.Ordinal),
            ErrorMessage = result.ErrorMessage,
            StartedAt = result.StartedAt,
            CompletedAt = result.CompletedAt,
            CollectionIteratorItemCount = collectionIteratorItemCount,
            CollectionIteratorRepeatedBlueprintNodeCount = collectionIteratorRepeatedBlueprintNodeCount,
            CollectionIteratorBatchSize = collectionIteratorBatchSize,
            CreatedAt = now,
            UpdatedAt = now
        };

        var (_, persistedRecord) = await _finalizationRepository.CreateIfNotExistsAsync(
            record,
            cancellationToken);
        return persistedRecord;
    }

    private async Task<T> RunWithRetryableErrorCheckpointAsync<T>(
        string recordId,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            await RecordRetryableErrorAsync(recordId, ex, cancellationToken);
            throw;
        }
    }

    private async Task RunWithRetryableErrorCheckpointAsync(
        string recordId,
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        await RunWithRetryableErrorCheckpointAsync(
            recordId,
            async () =>
            {
                await action();
                return true;
            },
            cancellationToken);
    }

    private Task<ProcessStepFinalizationRecord> AdvanceStageAsync(
        string recordId,
        ProcessStepFinalizationStage stage,
        bool trimResultSnapshot,
        CancellationToken cancellationToken) =>
        UpdateFinalizationRecordAsync(
            recordId,
            record =>
            {
                if (HasReached(record, stage))
                {
                    return false;
                }

                record.Stage = stage;
                record.UpdatedAt = DateTimeOffset.UtcNow;
                record.LastErrorKind = null;
                record.LastErrorMessage = null;
                record.LastErrorAt = null;

                if (trimResultSnapshot)
                {
                    TrimResultSnapshot(record);
                }

                return true;
            },
            cancellationToken);

    private Task RecordRetryableErrorAsync(
        string recordId,
        Exception exception,
        CancellationToken cancellationToken) =>
        UpdateFinalizationRecordAsync(
            recordId,
            record =>
            {
                record.LastErrorKind = ProcessStepFinalizationErrorKind.RetryableFinalizationError;
                record.LastErrorMessage = exception.Message;
                record.LastErrorAt = DateTimeOffset.UtcNow;
                record.RetryCount++;
                record.UpdatedAt = record.LastErrorAt;
                return true;
            },
            cancellationToken);

    private async Task<ProcessStepFinalizationRecord> UpdateFinalizationRecordAsync(
        string recordId,
        Func<ProcessStepFinalizationRecord, bool> mutate,
        CancellationToken cancellationToken)
    {
        var processStepId = Guid.Parse(recordId);
        for (var attempt = 0; attempt < MaxFinalizationUpdateAttempts; attempt++)
        {
            var record = await _finalizationRepository.GetAsync(
                    processStepId,
                    cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Finalization record {recordId} was not found.");

            if (!mutate(record))
            {
                return record;
            }

            if (await _finalizationRepository.TryUpdateAsync(record, cancellationToken))
            {
                return record;
            }
        }

        throw new InvalidOperationException(
            $"Could not update finalization record {recordId} after {MaxFinalizationUpdateAttempts} attempts.");
    }

    private static bool HasReached(
        ProcessStepFinalizationRecord record,
        ProcessStepFinalizationStage stage) =>
        record.Stage >= stage;

    private static void TrimResultSnapshot(ProcessStepFinalizationRecord record)
    {
        record.EffectiveInputData = null;
        record.OutputData = null;
        record.GlobalState = null;
        record.TimingsMs = null;
    }

    private static Dictionary<string, JsonElement>? NormalizeJsonMap(
        Dictionary<string, JsonElement>? values) =>
        values is null
            ? null
            : CloneJsonMap(values);

    private static Dictionary<string, JsonElement> CloneJsonMap(
        IReadOnlyDictionary<string, JsonElement> values) =>
        values.ToDictionary(
            entry => entry.Key,
            entry => NormalizeJsonElement(entry.Value),
            StringComparer.Ordinal);

    private static JsonElement NormalizeJsonElement(JsonElement element)
    {
        var node = BuildNormalizedNode(element);
        var normalizedJson = node?.ToJsonString(NormalizedJsonOptions) ?? "null";
        using var normalizedDocument = JsonDocument.Parse(normalizedJson);
        return normalizedDocument.RootElement.Clone();
    }

    private static JsonNode? BuildNormalizedNode(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => BuildNormalizedObject(element),
            JsonValueKind.Array => BuildNormalizedArray(element),
            JsonValueKind.String => JsonValue.Create(element.GetString()),
            JsonValueKind.Number => BuildNormalizedNumberValue(element),
            JsonValueKind.True or JsonValueKind.False => JsonValue.Create(element.GetBoolean()),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => throw new InvalidOperationException($"Unsupported JSON value kind '{element.ValueKind}'.")
        };

    private static JsonObject BuildNormalizedObject(JsonElement element)
    {
        var normalizedObject = new JsonObject();
        foreach (var property in element.EnumerateObject())
        {
            normalizedObject[property.Name] = BuildNormalizedNode(property.Value);
        }

        return normalizedObject;
    }

    private static JsonArray BuildNormalizedArray(JsonElement element)
    {
        var normalizedArray = new JsonArray();
        foreach (var item in element.EnumerateArray())
        {
            normalizedArray.Add(BuildNormalizedNode(item));
        }

        return normalizedArray;
    }

    private static JsonValue BuildNormalizedNumberValue(JsonElement element)
    {
        if (element.TryGetInt64(out var int64Value))
        {
            return JsonValue.Create(int64Value)!;
        }

        if (element.TryGetDecimal(out var decimalValue))
        {
            return JsonValue.Create(decimalValue)!;
        }

        return JsonValue.Create(element.GetDouble())!;
    }
}
