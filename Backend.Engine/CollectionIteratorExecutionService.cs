using System.Diagnostics;
using System.Text.Json;
using Backend.Database.Relational.Entities;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Models;
using Backend.Plugins.Communication;
using Backend.Repositories;
using Microsoft.Extensions.Options;

namespace Backend.Engine;

public sealed class CollectionIteratorExecutionService
{
    private const string CollectionInputPropertyName = "items";

    private readonly JobFinalizationService _jobFinalizationService;
    private readonly IProcessStepRepository _processStepRepository;
    private readonly IOptions<CollectionIteratorOptions> _options;
    private readonly ProcessLifecycleTelemetry _processLifecycleTelemetry;
    private readonly ILogger<CollectionIteratorExecutionService> _logger;

    public CollectionIteratorExecutionService(
        JobFinalizationService jobFinalizationService,
        IProcessStepRepository processStepRepository,
        IOptions<CollectionIteratorOptions> options,
        ProcessLifecycleTelemetry processLifecycleTelemetry,
        ILogger<CollectionIteratorExecutionService> logger)
    {
        _jobFinalizationService = jobFinalizationService;
        _processStepRepository = processStepRepository;
        _options = options;
        _processLifecycleTelemetry = processLifecycleTelemetry;
        _logger = logger;
    }

    public async Task ExecuteAsync(
        JobContext context,
        CancellationToken cancellationToken)
    {
        var claimStopwatch = Stopwatch.StartNew();
        var (stepClaimed, processStarted) = await context.TryMarkInProgressAsync(cancellationToken);
        claimStopwatch.Stop();
        _processLifecycleTelemetry.RecordStepClaim(context, claimStopwatch.Elapsed, stepClaimed);
        if (!stepClaimed)
        {
            await context.CompleteMessageAsync(cancellationToken);
            return;
        }

        var startedAt = context.StartedAt ?? DateTimeOffset.UtcNow;
        var previousActivity = Activity.Current;
        context.ExecutionActivity = _processLifecycleTelemetry.StartStepActivity(context);
        Activity.Current = context.ExecutionActivity ?? previousActivity;
        PluginExecutionResult? activityResult = null;
        Exception? activityException = null;
        try
        {
            _processLifecycleTelemetry.LogStepStarted(context, startedAt);
            if (processStarted)
            {
                _processLifecycleTelemetry.LogProcessStarted(context, startedAt);
            }

            var hydrationStopwatch = Stopwatch.StartNew();
            var materializedInput = await HydrateInputAsync(context, cancellationToken);
            hydrationStopwatch.Stop();

            var items = GetCollectionItems(context, materializedInput);
            ValidateItemLimit(context, items.Count);
            ValidateItemValues(context, items);

            var completedAt = DateTimeOffset.UtcNow;
            var outputData = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var effectiveInputData = BlueprintInputMaterializer.BuildChannelPayload(
                context.InputChannelName,
                materializedInput);
            var result = new PluginExecutionResult(
                context.ProcessStepId,
                Succeeded: true,
                EffectiveInputData: effectiveInputData,
                OutputData: outputData,
                GlobalState: null,
                StartedAt: startedAt,
                CompletedAt: completedAt,
                TimingsMs: new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["hydrationMs"] = hydrationStopwatch.Elapsed.TotalMilliseconds,
                    ["executeMs"] = 0
                },
                ErrorMessage: null);
            activityResult = result;

            var repeatedNodeCount = CountRepeatedBlueprintNodes(context.BlueprintNode, context.ExecutableBlueprint);
            _processLifecycleTelemetry.RecordRecordedStepDuration(context, result.CompletedAt);
            _processLifecycleTelemetry.RecordPluginPhaseTimings(context, result.TimingsMs);

            var finalizationResult = await _jobFinalizationService.FinalizeCollectionIteratorSuccessAsync(
                context,
                result,
                repeatedNodeCount,
                items.Count,
                _options.Value.BatchSize,
                cancellationToken);

            _processLifecycleTelemetry.RecordCollectionIteratorFanOut(
                context,
                finalizationResult.ItemCount,
                finalizationResult.QueuedStepCount,
                _options.Value.BatchSize,
                finalizationResult.DownstreamActivationElapsed);
            if (finalizationResult.ItemCount == 0)
            {
                _processLifecycleTelemetry.RecordCollectionIteratorSkippedEmpty(context);
            }

            _logger.LogInformation(
                "Collection Iterator expanded {ItemCount} item(s) for process {ProcessId}, blueprint node {BlueprintNodeId}, collection property {CollectionProperty}.",
                finalizationResult.ItemCount,
                context.ProcessId,
                context.BlueprintNodeId,
                CollectionInputPropertyName);
        }
        catch (Exception ex) when (activityResult is null)
        {
            var failedAt = DateTimeOffset.UtcNow;
            var errorMessage =
                $"Collection Iterator failed for process {context.ProcessId}, blueprint node {context.BlueprintNodeId}, collection property '{CollectionInputPropertyName}': {ex.Message}";
            var result = new PluginExecutionResult(
                context.ProcessStepId,
                Succeeded: false,
                EffectiveInputData: null,
                OutputData: new Dictionary<string, JsonElement>(StringComparer.Ordinal),
                GlobalState: null,
                StartedAt: startedAt,
                CompletedAt: failedAt,
                TimingsMs: null,
                ErrorMessage: errorMessage);
            activityResult = result;
            activityException = ex;

            try
            {
                await _jobFinalizationService.FinalizeCollectionIteratorFailureAsync(
                    context,
                    result,
                    cancellationToken);
            }
            catch (Exception finalizationEx)
            {
                activityException = finalizationEx;
                throw;
            }

            _logger.LogError(
                ex,
                "Collection Iterator failed for process {ProcessId}, blueprint node {BlueprintNodeId}, collection property {CollectionProperty}.",
                context.ProcessId,
                context.BlueprintNodeId,
                CollectionInputPropertyName);
        }
        finally
        {
            _processLifecycleTelemetry.StopStepActivity(context, activityResult, activityException);
            Activity.Current = previousActivity;
        }
    }

    private async Task<Dictionary<string, JsonElement>> HydrateInputAsync(
        JobContext context,
        CancellationToken cancellationToken)
    {
        var plan = context.InputHydrationPlan
            ?? throw new InvalidOperationException(
                $"Process step {context.ProcessStepId} has no input hydration plan.");
        if (!string.Equals(plan.InputChannelName, context.InputChannelName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Hydration plan targets input channel '{plan.InputChannelName}', expected '{context.InputChannelName}'.");
        }

        var partialInputs = new List<IReadOnlyDictionary<string, JsonElement>>
        {
            plan.InitialInput
        };

        foreach (var source in plan.Sources)
        {
            if (source.InlineInput is not null)
            {
                partialInputs.Add(source.InlineInput);
                continue;
            }

            var sourceStep = await _processStepRepository.GetProcessStepById(
                Guid.Parse(source.SourceProcessStepId),
                context.ProcessId,
                cancellationToken);
            if (sourceStep.OutputData is null ||
                !sourceStep.OutputData.TryGetValue(source.SourceOutputChannelName, out var sourcePayload))
            {
                throw new InvalidOperationException(
                    $"Source process step '{source.SourceProcessStepId}' does not contain output channel '{source.SourceOutputChannelName}' for binding '{source.BindingId}'.");
            }

            partialInputs.Add(BlueprintInputMaterializer.MaterializePartialInput(
                sourcePayload,
                source.PropertyMappings.Select(mapping => new ExecutableBlueprintPropertyMapping
                {
                    SourceProperty = mapping.SourceProperty,
                    TargetProperty = mapping.TargetProperty
                }).ToList()));
        }

        return BlueprintInputMaterializer.MergeInputs(
            partialInputs,
            plan.DesignTimeInputOverrides,
            plan.RuntimeInputOverrides);
    }

    private IReadOnlyList<JsonElement> GetCollectionItems(
        JobContext context,
        IReadOnlyDictionary<string, JsonElement> materializedInput)
    {
        if (!materializedInput.TryGetValue(CollectionInputPropertyName, out var collectionValue))
        {
            throw new InvalidOperationException(
                $"Missing required collection input '{CollectionInputPropertyName}'.");
        }

        if (collectionValue.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Expected collection input '{CollectionInputPropertyName}' to be a JSON array, but found '{collectionValue.ValueKind}'.");
        }

        var items = collectionValue.EnumerateArray()
            .Select(item => item.Clone())
            .ToList();
        _logger.LogDebug(
            "Collection Iterator read {ItemCount} item(s) for process {ProcessId}, blueprint node {BlueprintNodeId}.",
            items.Count,
            context.ProcessId,
            context.BlueprintNodeId);
        return items;
    }

    private void ValidateItemLimit(JobContext context, int itemCount)
    {
        var maxItems = Math.Max(0, _options.Value.MaxItems);
        if (itemCount <= maxItems)
        {
            return;
        }

        _processLifecycleTelemetry.RecordCollectionIteratorLimitFailure(context, itemCount, maxItems);
        throw new InvalidOperationException(
            $"Item count {itemCount} exceeds the configured maximum of {maxItems}.");
    }

    private static void ValidateItemValues(JobContext context, IReadOnlyList<JsonElement> items)
    {
        var itemKindName = context.BlueprintNode.CollectionIteratorItemKind ?? WorkflowPropertyKind.String.ToString();
        var itemKind = WorkflowPropertyTypeName.ParseScalar(itemKindName);
        for (var index = 0; index < items.Count; index++)
        {
            if (IsCompatibleJsonValue(itemKind, items[index]))
            {
                continue;
            }

            throw new InvalidOperationException(
                $"Item at index {index} is not compatible with declared item type '{itemKindName}'.");
        }
    }

    private static bool IsCompatibleJsonValue(WorkflowPropertyKind kind, JsonElement value) =>
        kind switch
        {
            WorkflowPropertyKind.String => value.ValueKind == JsonValueKind.String,
            WorkflowPropertyKind.Integer => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            WorkflowPropertyKind.Decimal => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _),
            WorkflowPropertyKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            WorkflowPropertyKind.Guid => value.ValueKind == JsonValueKind.String &&
                Guid.TryParse(value.GetString(), out _),
            WorkflowPropertyKind.Date => value.ValueKind == JsonValueKind.String &&
                DateOnly.TryParse(value.GetString(), out _),
            WorkflowPropertyKind.DateTime => value.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(value.GetString(), out _),
            WorkflowPropertyKind.Enum => value.ValueKind == JsonValueKind.String,
            _ => false
        };

    private static int CountRepeatedBlueprintNodes(
        ExecutableBlueprintNode iteratorNode,
        ExecutableBlueprint executableBlueprint)
    {
        var visited = new HashSet<Guid>();
        var pending = new Stack<Guid>(iteratorNode.OutputChannels.Values
            .SelectMany(channel => channel.OutboundBindings)
            .Select(binding => binding.DestinationBlueprintNodeId));

        while (pending.Count > 0)
        {
            var nodeId = pending.Pop();
            if (nodeId == iteratorNode.Id ||
                !visited.Add(nodeId) ||
                !executableBlueprint.NodesById.TryGetValue(nodeId, out var node))
            {
                continue;
            }

            foreach (var nextNodeId in node.OutputChannels.Values
                         .SelectMany(channel => channel.OutboundBindings)
                         .Select(binding => binding.DestinationBlueprintNodeId))
            {
                pending.Push(nextNodeId);
            }
        }

        return visited.Count;
    }
}
