using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Database.Relational.Entities;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Microsoft.Extensions.Logging;

namespace Backend.Repositories;

public sealed class MissingRecoveredHandoffException(Guid? sourceNodeId, string targetField)
    : InvalidOperationException($"The Data Bridge requires '{targetField}' from Step '{sourceNodeId}'.")
{
    public Guid? SourceNodeId { get; } = sourceNodeId;
}

public sealed class PhasedProcessFinalizationService
{
    private const int MaxUpdateAttempts = 10;
    private static readonly JsonSerializerOptions NormalizedJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly IPhasedBlueprintRepository _phasedBlueprintRepository;
    private readonly IPhasedProcessRepository _phasedProcessRepository;
    private readonly IPhaseCorrelationRepository _phaseCorrelationRepository;
    private readonly IProcessInstanceRepository _processInstanceRepository;
    private readonly IProcessStepRepository _processStepRepository;
    private readonly ILogger<PhasedProcessFinalizationService> _logger;

    public PhasedProcessFinalizationService(
        IPhasedBlueprintRepository phasedBlueprintRepository,
        IPhasedProcessRepository phasedProcessRepository,
        IPhaseCorrelationRepository phaseCorrelationRepository,
        IProcessInstanceRepository processInstanceRepository,
        IProcessStepRepository processStepRepository,
        ILogger<PhasedProcessFinalizationService> logger)
    {
        _phasedBlueprintRepository = phasedBlueprintRepository;
        _phasedProcessRepository = phasedProcessRepository;
        _phaseCorrelationRepository = phaseCorrelationRepository;
        _processInstanceRepository = processInstanceRepository;
        _processStepRepository = processStepRepository;
        _logger = logger;
    }

    public async Task FinalizeCompletedPhaseAsync(
        ProcessPhaseMetadata metadata,
        int childBlueprintVersionId,
        Guid childProcessId,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken,
        ProcessInstanceRecord? recoveredProcess = null)
    {
        var version = await _phasedBlueprintRepository.GetVersionGraphAsync(
            metadata.PhasedBlueprintVersionId,
            cancellationToken);
        var sourcePhase = version.Phases.FirstOrDefault(phase => phase.Id.ToString() == metadata.PhaseId)
            ?? throw new InvalidOperationException($"Phased blueprint version {version.Id} does not contain phase {metadata.PhaseId}.");
        var transition = version.Transitions.FirstOrDefault(candidate => candidate.SourcePhaseId == sourcePhase.Id);
        var result = transition is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : await ExtractPhaseResultAsync(
                transition,
                childBlueprintVersionId,
                childProcessId,
                sourcePhase.VersionPolicy == PhasedBlueprintPhaseVersionPolicy.Latest,
                cancellationToken, recoveredProcess);

        PhaseCorrelationRecord? correlationRecord = null;
        if (transition is not null)
        {
            var targetPhase = version.Phases.First(phase => phase.Id == transition.DestinationPhaseId);
            var correlationValues = BuildCorrelationValues(transition, result);
            correlationRecord = new PhaseCorrelationRecord
            {
                id = ComputeCorrelationId(metadata.PhasedBlueprintVersionId, targetPhase.Id, correlationValues),
                TargetPhaseKey = BuildTargetPhaseKey(metadata.PhasedBlueprintVersionId, targetPhase.Id),
                PhasedProcessId = metadata.PhasedProcessId,
                PhasedBlueprintDefinitionId = metadata.PhasedBlueprintDefinitionId,
                PhasedBlueprintVersionId = metadata.PhasedBlueprintVersionId,
                TransitionId = transition.Id.ToString(),
                SourcePhaseId = sourcePhase.Id.ToString(),
                TargetPhaseId = targetPhase.Id.ToString(),
                TargetPhaseIndex = targetPhase.PhaseIndex,
                Values = correlationValues,
                CreatedAt = completedAt
            };
        }

        for (var attempt = 0; attempt < MaxUpdateAttempts; attempt++)
        {
            var parent = await _phasedProcessRepository.GetAsync(
                Guid.Parse(metadata.PhasedProcessId),
                metadata.PhasedBlueprintDefinitionId,
                cancellationToken);
            var phaseRecord = parent.Phases.FirstOrDefault(phase => phase.PhaseId == metadata.PhaseId)
                ?? throw new InvalidOperationException($"Phased process {metadata.PhasedProcessId} does not contain phase {metadata.PhaseId}.");

            // A replay may arrive after the next phase has already consumed its trigger.
            if (phaseRecord.Status == PhasedProcessStatus.Completed && phaseRecord.ChildProcessId == childProcessId.ToString())
                break;
            parent.PhaseResults[metadata.PhaseId] = CloneJsonMap(result);
            phaseRecord.Status = PhasedProcessStatus.Completed;
            phaseRecord.FailureMessage = null;
            phaseRecord.CompletedAt = completedAt;
            phaseRecord.BlueprintVersionId ??= childBlueprintVersionId;
            phaseRecord.ChildProcessId ??= childProcessId.ToString();
            parent.UpdatedAt = completedAt;

            if (transition is null)
            {
                parent.Status = PhasedProcessStatus.Completed;
                parent.CurrentPhaseId = metadata.PhaseId;
            }
            else
            {
                parent.Status = PhasedProcessStatus.WaitingForTrigger;
                parent.CurrentPhaseId = transition.DestinationPhaseId.ToString();
            }

            if (await _phasedProcessRepository.TryUpdateAsync(parent, cancellationToken))
            {
                break;
            }

            if (attempt == MaxUpdateAttempts - 1)
            {
                throw new InvalidOperationException(
                    $"Could not finalize phased process {metadata.PhasedProcessId} after {MaxUpdateAttempts} attempts.");
            }
        }

        if (correlationRecord is not null)
        {
            await _phaseCorrelationRepository.CreateAsync(correlationRecord, cancellationToken);
            _logger.LogInformation(
                "Registered correlation {CorrelationId} for phased process {PhasedProcessId} phase {TargetPhaseIndex}.",
                correlationRecord.Id,
                metadata.PhasedProcessId,
                correlationRecord.TargetPhaseIndex);
        }
    }

    public async Task MarkPhaseFailedAsync(
        ProcessPhaseMetadata metadata,
        int childBlueprintVersionId,
        Guid childProcessId,
        string? failureMessage,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxUpdateAttempts; attempt++)
        {
            var parent = await _phasedProcessRepository.GetAsync(
                Guid.Parse(metadata.PhasedProcessId),
                metadata.PhasedBlueprintDefinitionId,
                cancellationToken);
            var phaseRecord = parent.Phases.FirstOrDefault(phase => phase.PhaseId == metadata.PhaseId)
                ?? throw new InvalidOperationException($"Phased process {metadata.PhasedProcessId} does not contain phase {metadata.PhaseId}.");

            if (phaseRecord.Status == PhasedProcessStatus.Completed && phaseRecord.ChildProcessId == childProcessId.ToString())
                return;
            phaseRecord.Status = PhasedProcessStatus.Failed;
            phaseRecord.CompletedAt = failedAt;
            phaseRecord.BlueprintVersionId ??= childBlueprintVersionId;
            phaseRecord.ChildProcessId ??= childProcessId.ToString();
            phaseRecord.FailureMessage = failureMessage;
            parent.Status = PhasedProcessStatus.Failed;
            parent.CurrentPhaseId = metadata.PhaseId;
            parent.UpdatedAt = failedAt;

            if (await _phasedProcessRepository.TryUpdateAsync(parent, cancellationToken))
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"Could not mark phased process {metadata.PhasedProcessId} failed after {MaxUpdateAttempts} attempts.");
    }

    public static string BuildTargetPhaseKey(int phasedBlueprintVersionId, Guid targetPhaseId) =>
        $"{phasedBlueprintVersionId}:{targetPhaseId:N}";

    public static string ComputeCorrelationId(
        int phasedBlueprintVersionId,
        Guid targetPhaseId,
        IReadOnlyDictionary<string, JsonElement> correlationValues)
    {
        var canonicalPayload = JsonSerializer.Serialize(
            new
            {
                phasedBlueprintVersionId,
                targetPhaseId = targetPhaseId.ToString("N"),
                values = correlationValues
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => new
                    {
                        entry.Key,
                        Value = NormalizeJsonElement(entry.Value).GetRawText()
                    })
            },
            NormalizedJsonOptions);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload)));
    }

    private async Task<Dictionary<string, JsonElement>> ExtractPhaseResultAsync(
        PhasedBlueprintTransition transition,
        int childBlueprintVersionId,
        Guid childProcessId,
        bool resolveLatest,
        CancellationToken cancellationToken,
        ProcessInstanceRecord? recoveredProcess)
    {
        var sourceIds = resolveLatest
            ? await _phasedBlueprintRepository.ResolveHandoffNodeIdsAsync(childBlueprintVersionId,
                transition.HandoffMappings.Select(m => m.SourceBlueprintNodeId).OfType<Guid>(), cancellationToken)
            : null;
        var steps = await _processStepRepository.GetProcessStepsForProcess(
            childProcessId,
            cancellationToken);
        var effective = recoveredProcess is null ? steps : ProcessRecoveryState.EffectiveSteps(recoveredProcess, steps);
        var stepsByNode = effective
            .Where(step => Guid.TryParse(step.BlueprintNodeId, out _))
            .GroupBy(step => Guid.Parse(step.BlueprintNodeId))
            .ToDictionary(group => group.Key, group => group.ToList());

        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var mapping in transition.HandoffMappings)
        {
            if (mapping.SourceBlueprintNodeId is not Guid sourceNodeId ||
                string.IsNullOrWhiteSpace(mapping.SourceChannelName) ||
                !stepsByNode.TryGetValue(sourceIds?[sourceNodeId] ?? sourceNodeId, out var sourceSteps))
            {
                if (recoveredProcess is not null)
                    throw new MissingRecoveredHandoffException(mapping.SourceBlueprintNodeId, mapping.TargetProperty);
                continue;
            }

            var step = sourceSteps
                .Where(candidate => candidate.Status == ProcessStepStatus.Completed)
                .OrderByDescending(candidate => candidate.CompletedAt)
                .FirstOrDefault();
            if (step?.OutputData is null ||
                !step.OutputData.TryGetValue(mapping.SourceChannelName, out var channelValue) ||
                channelValue.ValueKind != JsonValueKind.Object ||
                !channelValue.TryGetProperty(mapping.SourceProperty, out var propertyValue) ||
                (recoveredProcess is not null && propertyValue.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined))
            {
                if (recoveredProcess is not null)
                    throw new MissingRecoveredHandoffException(Guid.Parse(step?.BlueprintNodeId ?? sourceNodeId.ToString()), mapping.TargetProperty);
                continue;
            }

            SetHandoffResult(result, mapping, propertyValue);
        }

        return result;
    }

    private static void SetHandoffResult(
        Dictionary<string, JsonElement> result,
        PhasedBlueprintHandoffMapping mapping,
        JsonElement value)
    {
        var normalizedValue = NormalizeJsonElement(value);
        result[BuildHandoffResultKey(mapping)] = normalizedValue;
        if (!result.ContainsKey(mapping.TargetProperty))
        {
            result[mapping.TargetProperty] = normalizedValue.Clone();
        }
    }

    public static string BuildHandoffResultKey(PhasedBlueprintHandoffMapping mapping) =>
        $"{mapping.TargetBlueprintNodeId:N}:{mapping.TargetProperty}";

    private static Dictionary<string, JsonElement> BuildCorrelationValues(
        PhasedBlueprintTransition transition,
        IReadOnlyDictionary<string, JsonElement> phaseResult)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var mapping in transition.CorrelationMappings)
        {
            if (!phaseResult.TryGetValue(mapping.SourceResultProperty, out var value))
            {
                throw new InvalidOperationException(
                    $"Phase result did not contain correlation source property '{mapping.SourceResultProperty}'.");
            }

            values[mapping.TriggerProperty] = NormalizeJsonElement(value);
        }

        return values;
    }

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
        using var document = JsonDocument.Parse(normalizedJson);
        return document.RootElement.Clone();
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
        var normalized = new JsonObject();
        foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            normalized[property.Name] = BuildNormalizedNode(property.Value);
        }

        return normalized;
    }

    private static JsonArray BuildNormalizedArray(JsonElement element)
    {
        var normalized = new JsonArray();
        foreach (var item in element.EnumerateArray())
        {
            normalized.Add(BuildNormalizedNode(item));
        }

        return normalized;
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
