using System.Net.Http.Json;
using System.Text.Json;
using Backend.Models;
using Backend.Models.InputModels;
using Backend.Models.ViewModels;

namespace Backend.BlueprintSeeder;

internal sealed class BlueprintImportClient
{
    private static readonly HashSet<string> RetryableCodes =
    [
        BlueprintImportErrorCodes.TaskDefinitionMissing,
        BlueprintImportErrorCodes.TaskLaneMissing
    ];

    private readonly HttpClient _httpClient;

    public BlueprintImportClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<BlueprintImportAttemptResult> ImportAsync(
        ExportedBlueprintInputModel payload,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "api/blueprints/import",
            payload,
            cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var viewModel = await response.Content.ReadFromJsonAsync<BlueprintImportViewModel>(cancellationToken);
            if (viewModel is null)
            {
                throw new InvalidOperationException("Blueprint import response payload was empty.");
            }

            return BlueprintImportAttemptResult.Success(viewModel);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var codes = ExtractErrorCodes(body);
        return BlueprintImportAttemptResult.Failure(
            (int)response.StatusCode,
            body,
            codes,
            codes.Count > 0 && codes.All(code => RetryableCodes.Contains(code)));
    }

    public async Task<PhaseSeriesImportAttemptResult> ImportPhaseSeriesAsync(
        ExportedPhasedBlueprintInputModel payload,
        CancellationToken cancellationToken)
    {
        var existingPhaseSeries = await _httpClient.GetFromJsonAsync<List<PhasedBlueprintViewModel>>(
            "api/phased-blueprints",
            cancellationToken)
            ?? [];
        var existing = existingPhaseSeries.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, payload.DefinitionKey.Trim(), StringComparison.Ordinal));
        if (existing is not null)
        {
            return PhaseSeriesImportAttemptResult.Success(
                existing.Key,
                existing.Id,
                "MatchedExistingDefinition");
        }

        var blueprints = await _httpClient.GetFromJsonAsync<List<BlueprintViewModel>>(
            "api/blueprints",
            cancellationToken)
            ?? [];
        var phaseSeriesPayload = BuildPhaseSeriesPayload(payload, blueprints);
        using var response = await _httpClient.PostAsJsonAsync(
            "api/phased-blueprints",
            phaseSeriesPayload,
            cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var viewModel = await response.Content.ReadFromJsonAsync<PhasedBlueprintViewModel>(cancellationToken);
            if (viewModel is null)
            {
                throw new InvalidOperationException("Phase series import response payload was empty.");
            }

            return PhaseSeriesImportAttemptResult.Success(
                viewModel.Key,
                viewModel.Id,
                "CreatedDefinition");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return PhaseSeriesImportAttemptResult.Failure(
            (int)response.StatusCode,
            body,
            retryable: false);
    }

    private static PhasedBlueprintInputModel BuildPhaseSeriesPayload(
        ExportedPhasedBlueprintInputModel payload,
        IReadOnlyCollection<BlueprintViewModel> blueprints)
    {
        var blueprintByKey = blueprints.ToDictionary(blueprint => blueprint.Key, StringComparer.Ordinal);
        var phases = payload.Phases
            .OrderBy(phase => phase.PhaseIndex)
            .Select(phase =>
            {
                if (!blueprintByKey.TryGetValue(phase.BlueprintDefinitionKey.Trim(), out var blueprint))
                {
                    throw new InvalidDataException(
                        $"Blueprint '{phase.BlueprintDefinitionKey}' was not found for phase series '{payload.DefinitionKey}'.");
                }

                var version = ResolveBlueprintVersion(blueprint, phase.BlueprintVersion);
                return new PhasedBlueprintPhaseInputModel
                {
                    PhaseId = phase.PhaseId,
                    PhaseIndex = phase.PhaseIndex,
                    Name = phase.Name,
                    BlueprintDefinitionId = blueprint.Id,
                    BlueprintVersionId = version.BlueprintVersionId,
                    VersionPolicy = phase.VersionPolicy
                };
            })
            .ToList();

        var phaseImportById = payload.Phases.ToDictionary(phase => phase.PhaseId);
        var transitions = payload.Transitions
            .Select(transition =>
            {
                var sourcePhaseImport = phaseImportById[transition.SourcePhaseId];
                var destinationPhaseImport = phaseImportById[transition.DestinationPhaseId];
                var sourceVersion = ResolveBlueprintVersion(
                    blueprintByKey[sourcePhaseImport.BlueprintDefinitionKey],
                    sourcePhaseImport.BlueprintVersion);
                var destinationVersion = ResolveBlueprintVersion(
                    blueprintByKey[destinationPhaseImport.BlueprintDefinitionKey],
                    destinationPhaseImport.BlueprintVersion);

                return new PhasedBlueprintTransitionInputModel
                {
                    TransitionId = transition.TransitionId,
                    SourcePhaseId = transition.SourcePhaseId,
                    DestinationPhaseId = transition.DestinationPhaseId,
                    HandoffMappings = transition.HandoffMappings.Select(mapping => new PhasedBlueprintHandoffMappingInputModel
                    {
                        SourceKind = mapping.SourceKind,
                        SourceBlueprintNodeId = string.Equals(mapping.SourceKind, "GlobalState", StringComparison.OrdinalIgnoreCase)
                            ? null
                            : ResolveNodeId(sourceVersion, mapping.SourceBlueprintNodeKey),
                        SourceChannelName = mapping.SourceChannelName,
                        SourceProperty = mapping.SourceProperty,
                        BridgePosition = mapping.BridgePosition,
                        TargetBlueprintNodeId = ResolveNodeId(destinationVersion, mapping.TargetBlueprintNodeKey),
                        TargetProperty = mapping.TargetProperty
                    }).ToList(),
                    CorrelationMappings = transition.CorrelationMappings
                };
            })
            .ToList();

        return new PhasedBlueprintInputModel
        {
            Key = payload.DefinitionKey.Trim(),
            Name = payload.DisplayName.Trim(),
            Description = payload.Description,
            Phases = phases,
            Transitions = transitions
        };
    }

    private static BlueprintVersionViewModel ResolveBlueprintVersion(
        BlueprintViewModel blueprint,
        int? versionNumber)
    {
        if (blueprint.Versions.Count == 0)
        {
            throw new InvalidDataException($"Blueprint '{blueprint.Key}' has no versions.");
        }

        var version = versionNumber.HasValue
            ? blueprint.Versions.FirstOrDefault(candidate => candidate.Version == versionNumber.Value)
            : blueprint.Versions.OrderByDescending(candidate => candidate.Version).FirstOrDefault();

        return version
            ?? throw new InvalidDataException($"Blueprint '{blueprint.Key}' does not contain version {versionNumber}.");
    }

    private static Guid ResolveNodeId(
        BlueprintVersionViewModel blueprintVersion,
        Guid? nodeKey)
    {
        if (nodeKey is not Guid concreteNodeKey || concreteNodeKey == Guid.Empty)
        {
            throw new InvalidDataException("Phase handoff node keys must be non-empty GUIDs.");
        }

        return blueprintVersion.Tasks.Any(task => task.InstanceId == concreteNodeKey)
            ? concreteNodeKey
            : throw new InvalidDataException(
                $"Blueprint version {blueprintVersion.BlueprintVersionId} does not contain node '{concreteNodeKey}'.");
    }

    private static IReadOnlyList<string> ExtractErrorCodes(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("errors", out var errorsElement) ||
                errorsElement.ValueKind is not JsonValueKind.Array)
            {
                return [];
            }

            return errorsElement.EnumerateArray()
                .Where(element => element.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String)
                .Select(element => element.GetProperty("code").GetString())
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
