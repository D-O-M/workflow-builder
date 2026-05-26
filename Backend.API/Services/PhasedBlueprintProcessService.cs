using System.Text.Json;
using Backend.Database.Relational.Entities;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Models.InputModels;
using Backend.Models.ViewModels;
using Backend.Repositories;

namespace Backend.API.Services;

public sealed class PhasedBlueprintProcessService
{
    private const int MaxUpdateAttempts = 10;

    private readonly IPhasedBlueprintRepository _phasedBlueprintRepository;
    private readonly IPhasedProcessRepository _phasedProcessRepository;
    private readonly IPhaseCorrelationRepository _phaseCorrelationRepository;
    private readonly IBlueprintRepository _blueprintRepository;
    private readonly BlueprintProcessService _blueprintProcessService;
    private readonly ProcessQueryService _processQueryService;

    public PhasedBlueprintProcessService(
        IPhasedBlueprintRepository phasedBlueprintRepository,
        IPhasedProcessRepository phasedProcessRepository,
        IPhaseCorrelationRepository phaseCorrelationRepository,
        IBlueprintRepository blueprintRepository,
        BlueprintProcessService blueprintProcessService,
        ProcessQueryService processQueryService)
    {
        _phasedBlueprintRepository = phasedBlueprintRepository;
        _phasedProcessRepository = phasedProcessRepository;
        _phaseCorrelationRepository = phaseCorrelationRepository;
        _blueprintRepository = blueprintRepository;
        _blueprintProcessService = blueprintProcessService;
        _processQueryService = processQueryService;
    }

    public async Task<PhasedProcessAcceptedViewModel> StartAsync(
        int phasedBlueprintDefinitionId,
        PhasedBlueprintProcessInputModel model,
        CancellationToken cancellationToken)
    {
        var versionId = await _phasedBlueprintRepository.GetLatestVersionIdAsync(
            phasedBlueprintDefinitionId,
            cancellationToken);
        var version = await _phasedBlueprintRepository.GetVersionGraphAsync(
            versionId,
            cancellationToken);
        if (version.PhasedBlueprintDefinitionId != phasedBlueprintDefinitionId)
        {
            throw new EntityNotFoundException<PhasedBlueprintViewModel>(phasedBlueprintDefinitionId);
        }

        var firstPhase = version.Phases
            .OrderBy(phase => phase.PhaseIndex)
            .FirstOrDefault()
            ?? throw new InvalidDataException("Phased blueprint versions must contain at least one phase.");
        var firstBlueprintVersionId = await ResolveBlueprintVersionIdAsync(firstPhase, cancellationToken);
        var acceptedAt = DateTimeOffset.UtcNow;
        var phasedProcessId = Guid.NewGuid();
        var childProcessId = Guid.NewGuid();
        var parent = new PhasedProcessRecord
        {
            id = phasedProcessId.ToString(),
            PhasedBlueprintDefinitionId = phasedBlueprintDefinitionId.ToString(),
            PhasedBlueprintVersionId = version.Id,
            CreatedAt = acceptedAt,
            UpdatedAt = acceptedAt,
            Status = PhasedProcessStatus.InProgress,
            CurrentPhaseId = firstPhase.Id.ToString(),
            Phases = version.Phases
                .OrderBy(phase => phase.PhaseIndex)
                .Select(phase => new PhasedProcessPhaseRecord
                {
                    PhaseId = phase.Id.ToString(),
                    PhaseIndex = phase.PhaseIndex,
                    PhaseName = phase.Name,
                    BlueprintDefinitionId = phase.BlueprintDefinitionId,
                    BlueprintVersionId = phase.Id == firstPhase.Id ? firstBlueprintVersionId : phase.BlueprintVersionId,
                    ChildProcessId = phase.Id == firstPhase.Id ? childProcessId.ToString() : null,
                    Status = phase.Id == firstPhase.Id ? PhasedProcessStatus.InProgress : PhasedProcessStatus.Pending,
                    StartedAt = phase.Id == firstPhase.Id ? acceptedAt : null,
                    TriggerInput = phase.Id == firstPhase.Id
                        ? CloneJsonMap(model.InitialTaskInput)
                        : new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                })
                .ToList()
        };

        await _phasedProcessRepository.CreateAsync(parent, cancellationToken);

        var metadata = CreatePhaseMetadata(parent, firstPhase);
        try
        {
            var childInput = new BlueprintProcessInputModel
            {
                InitialTaskInput = CloneJsonMap(model.InitialTaskInput),
                NodeOverrides = CloneNodeOverrides(model.NodeOverrides)
            };

            await _blueprintProcessService.StartVersionAsync(
                firstPhase.BlueprintDefinitionId,
                firstBlueprintVersionId,
                childInput,
                metadata,
                childProcessId,
                cancellationToken);
        }
        catch (Exception exception)
        {
            await MarkReservedPhaseFailedAsync(
                phasedBlueprintDefinitionId,
                phasedProcessId,
                firstPhase.Id,
                firstBlueprintVersionId,
                childProcessId,
                exception.Message,
                DateTimeOffset.UtcNow,
                cancellationToken);
            throw;
        }

        return new PhasedProcessAcceptedViewModel
        {
            PhasedProcessId = phasedProcessId,
            PhasedBlueprintDefinitionId = phasedBlueprintDefinitionId,
            PhasedBlueprintVersionId = version.Id,
            ChildProcessId = childProcessId,
            PhaseIndex = firstPhase.PhaseIndex,
            AcceptedAt = acceptedAt
        };
    }

    public async Task<PhasedProcessAcceptedViewModel> TriggerPhaseAsync(
        int phasedBlueprintDefinitionId,
        int targetPhaseIndex,
        PhasedBlueprintTriggerInputModel model,
        CancellationToken cancellationToken)
    {
        if (targetPhaseIndex <= 0)
        {
            throw new InvalidDataException("Phase triggers can only start phases after phase 1.");
        }

        var versionId = model.PhasedBlueprintVersionId
            ?? await _phasedBlueprintRepository.GetLatestVersionIdAsync(phasedBlueprintDefinitionId, cancellationToken);
        var version = await _phasedBlueprintRepository.GetVersionGraphAsync(versionId, cancellationToken);
        if (version.PhasedBlueprintDefinitionId != phasedBlueprintDefinitionId)
        {
            throw new EntityNotFoundException<PhasedBlueprintViewModel>(phasedBlueprintDefinitionId);
        }

        var targetPhase = version.Phases.FirstOrDefault(phase => phase.PhaseIndex == targetPhaseIndex)
            ?? throw new InvalidDataException($"Phased blueprint version {version.Id} does not contain phase index {targetPhaseIndex}.");
        var transition = version.Transitions.FirstOrDefault(candidate => candidate.DestinationPhaseId == targetPhase.Id)
            ?? throw new InvalidDataException($"Phase index {targetPhaseIndex} does not have an incoming transition.");
        var triggerCorrelationValues = BuildTriggerCorrelationValues(transition, model.TriggerInput);
        var correlationId = PhasedProcessFinalizationService.ComputeCorrelationId(
            version.Id,
            targetPhase.Id,
            triggerCorrelationValues);
        var correlation = await _phaseCorrelationRepository.GetAsync(
            correlationId,
            PhasedProcessFinalizationService.BuildTargetPhaseKey(version.Id, targetPhase.Id),
            cancellationToken);
        if (correlation is null)
        {
            throw new EntityNotFoundException<PhaseCorrelationRecord>(correlationId);
        }

        var targetBlueprintVersionId = await ResolveBlueprintVersionIdAsync(targetPhase, cancellationToken);
        var reservedChildProcessId = Guid.NewGuid();
        var acceptedAt = DateTimeOffset.UtcNow;
        PhasedProcessRecord parent;
        PhasedProcessPhaseRecord phaseRecord;
        for (var attempt = 0; attempt < MaxUpdateAttempts; attempt++)
        {
            parent = await _phasedProcessRepository.GetAsync(
                Guid.Parse(correlation.PhasedProcessId),
                correlation.PhasedBlueprintDefinitionId,
                cancellationToken);
            phaseRecord = parent.Phases.First(candidate => candidate.PhaseId == targetPhase.Id.ToString());
            if (!string.IsNullOrWhiteSpace(phaseRecord.ChildProcessId) &&
                Guid.TryParse(phaseRecord.ChildProcessId, out var existingChildProcessId))
            {
                return new PhasedProcessAcceptedViewModel
                {
                    PhasedProcessId = Guid.Parse(parent.Id),
                    PhasedBlueprintDefinitionId = phasedBlueprintDefinitionId,
                    PhasedBlueprintVersionId = version.Id,
                    ChildProcessId = existingChildProcessId,
                    PhaseIndex = targetPhase.PhaseIndex,
                    AcceptedAt = phaseRecord.StartedAt ?? parent.UpdatedAt ?? parent.CreatedAt
                };
            }

            phaseRecord.ChildProcessId = reservedChildProcessId.ToString();
            phaseRecord.BlueprintVersionId = targetBlueprintVersionId;
            phaseRecord.Status = PhasedProcessStatus.InProgress;
            phaseRecord.StartedAt = acceptedAt;
            phaseRecord.TriggerInput = CloneJsonMap(model.TriggerInput);
            parent.Status = PhasedProcessStatus.InProgress;
            parent.CurrentPhaseId = targetPhase.Id.ToString();
            parent.UpdatedAt = acceptedAt;

            if (await _phasedProcessRepository.TryUpdateAsync(parent, cancellationToken))
            {
                break;
            }

            if (attempt == MaxUpdateAttempts - 1)
            {
                throw new InvalidOperationException(
                    $"Could not reserve target phase {targetPhase.Id} for phased process {parent.Id}.");
            }
        }

        parent = await _phasedProcessRepository.GetAsync(
            Guid.Parse(correlation.PhasedProcessId),
            correlation.PhasedBlueprintDefinitionId,
            cancellationToken);
        var nodeOverrides = BuildTriggeredNodeOverrides(parent, transition, model.NodeOverrides);
        var metadata = CreatePhaseMetadata(parent, targetPhase);
        try
        {
            await _blueprintProcessService.StartVersionAsync(
                targetPhase.BlueprintDefinitionId,
                targetBlueprintVersionId,
                new BlueprintProcessInputModel
                {
                    InitialTaskInput = CloneJsonMap(model.TriggerInput),
                    NodeOverrides = nodeOverrides
                },
                metadata,
                reservedChildProcessId,
                cancellationToken);
        }
        catch (Exception exception)
        {
            await MarkReservedPhaseFailedAsync(
                phasedBlueprintDefinitionId,
                Guid.Parse(correlation.PhasedProcessId),
                targetPhase.Id,
                targetBlueprintVersionId,
                reservedChildProcessId,
                exception.Message,
                DateTimeOffset.UtcNow,
                cancellationToken);
            throw;
        }

        return new PhasedProcessAcceptedViewModel
        {
            PhasedProcessId = Guid.Parse(correlation.PhasedProcessId),
            PhasedBlueprintDefinitionId = phasedBlueprintDefinitionId,
            PhasedBlueprintVersionId = version.Id,
            ChildProcessId = reservedChildProcessId,
            PhaseIndex = targetPhase.PhaseIndex,
            AcceptedAt = acceptedAt
        };
    }

    public async Task<PhasedProcessDetailViewModel> GetDetailAsync(
        int phasedBlueprintDefinitionId,
        Guid phasedProcessId,
        CancellationToken cancellationToken)
    {
        var parent = await _phasedProcessRepository.GetAsync(
            phasedProcessId,
            phasedBlueprintDefinitionId,
            cancellationToken);
        var version = await _phasedBlueprintRepository.GetVersionGraphAsync(
            parent.PhasedBlueprintVersionId,
            cancellationToken);

        var phases = new List<PhasedProcessPhaseViewModel>();
        foreach (var phase in parent.Phases.OrderBy(phase => phase.PhaseIndex))
        {
            var phaseDefinition = version.Phases.First(candidate => candidate.Id.ToString() == phase.PhaseId);
            ProcessDetailViewModel? childProcess = null;
            if (!string.IsNullOrWhiteSpace(phase.ChildProcessId) &&
                Guid.TryParse(phase.ChildProcessId, out var childProcessId))
            {
                try
                {
                    childProcess = await _processQueryService.GetProcessDetailAsync(
                        phase.BlueprintDefinitionId,
                        childProcessId,
                        cancellationToken);
                }
                catch (EntityNotFoundException<ProcessDetailViewModel>)
                {
                    childProcess = null;
                }
            }

            parent.PhaseResults.TryGetValue(phase.PhaseId, out var phaseResult);
            phases.Add(new PhasedProcessPhaseViewModel
            {
                PhaseId = Guid.Parse(phase.PhaseId),
                PhaseIndex = phase.PhaseIndex,
                PhaseName = phase.PhaseName,
                BlueprintDefinitionId = phase.BlueprintDefinitionId,
                BlueprintName = phaseDefinition.BlueprintDefinition.DisplayName,
                BlueprintVersionId = phase.BlueprintVersionId,
                ChildProcessId = string.IsNullOrWhiteSpace(phase.ChildProcessId)
                    ? null
                    : Guid.Parse(phase.ChildProcessId),
                Status = phase.Status.ToString(),
                StartedAt = phase.StartedAt,
                CompletedAt = phase.CompletedAt,
                FailureMessage = phase.FailureMessage,
                PhaseResult = phaseResult is null ? null : JsonSerializer.SerializeToElement(phaseResult),
                ChildProcess = childProcess
            });
        }

        return new PhasedProcessDetailViewModel
        {
            PhasedProcessId = phasedProcessId,
            PhasedBlueprintDefinitionId = phasedBlueprintDefinitionId,
            PhasedBlueprintName = version.PhasedBlueprintDefinition.DisplayName,
            PhasedBlueprintVersionId = version.Id,
            PhasedBlueprintVersionNumber = version.Version,
            Status = parent.Status.ToString(),
            CreatedAt = parent.CreatedAt,
            UpdatedAt = parent.UpdatedAt,
            Phases = phases
        };
    }

    private async Task<int> ResolveBlueprintVersionIdAsync(
        PhasedBlueprintPhase phase,
        CancellationToken cancellationToken) =>
        phase.VersionPolicy switch
        {
            PhasedBlueprintPhaseVersionPolicy.Latest => await _blueprintRepository.GetLatestBlueprintVersionId(
                phase.BlueprintDefinitionId,
                cancellationToken),
            _ => phase.BlueprintVersionId
                ?? throw new InvalidDataException("Pinned phases must reference a blueprint version.")
        };

    private static Dictionary<string, JsonElement> BuildTriggerCorrelationValues(
        PhasedBlueprintTransition transition,
        IReadOnlyDictionary<string, JsonElement> triggerInput)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var mapping in transition.CorrelationMappings)
        {
            if (!triggerInput.TryGetValue(mapping.TriggerProperty, out var triggerValue))
            {
                throw new InvalidDataException(
                    $"Trigger payload is missing correlation property '{mapping.TriggerProperty}'.");
            }

            values[mapping.TriggerProperty] = triggerValue.Clone();
        }

        return values;
    }

    private static Dictionary<Guid, Dictionary<string, JsonElement>> BuildTriggeredNodeOverrides(
        PhasedProcessRecord parent,
        PhasedBlueprintTransition transition,
        IReadOnlyDictionary<Guid, Dictionary<string, JsonElement>> baseNodeOverrides)
    {
        var nodeOverrides = CloneNodeOverrides(baseNodeOverrides);
        if (!parent.PhaseResults.TryGetValue(transition.SourcePhaseId.ToString(), out var handoffResult))
        {
            throw new InvalidOperationException(
                $"Phased process {parent.Id} does not have a result for source phase {transition.SourcePhaseId}.");
        }

        foreach (var mapping in transition.HandoffMappings)
        {
            if (handoffResult.TryGetValue(BuildHandoffResultKey(mapping), out var handoffValue))
            {
                if (!nodeOverrides.TryGetValue(mapping.TargetBlueprintNodeId, out var propertyOverrides))
                {
                    propertyOverrides = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    nodeOverrides[mapping.TargetBlueprintNodeId] = propertyOverrides;
                }

                propertyOverrides[mapping.TargetProperty] = handoffValue.Clone();
            }
        }

        return nodeOverrides;
    }

    private static string BuildHandoffResultKey(PhasedBlueprintHandoffMapping mapping) =>
        $"{mapping.TargetBlueprintNodeId:N}:{mapping.TargetProperty}";

    private static ProcessPhaseMetadata CreatePhaseMetadata(
        PhasedProcessRecord parent,
        PhasedBlueprintPhase phase) =>
        new()
        {
            PhasedProcessId = parent.Id,
            PhasedBlueprintDefinitionId = int.Parse(parent.PhasedBlueprintDefinitionId),
            PhasedBlueprintVersionId = parent.PhasedBlueprintVersionId,
            PhaseId = phase.Id.ToString(),
            PhaseIndex = phase.PhaseIndex,
            PhaseName = phase.Name
        };

    private async Task MarkReservedPhaseFailedAsync(
        int phasedBlueprintDefinitionId,
        Guid phasedProcessId,
        Guid phaseId,
        int blueprintVersionId,
        Guid childProcessId,
        string failureMessage,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxUpdateAttempts; attempt++)
        {
            var parent = await _phasedProcessRepository.GetAsync(
                phasedProcessId,
                phasedBlueprintDefinitionId,
                cancellationToken);
            var phaseRecord = parent.Phases.First(candidate => candidate.PhaseId == phaseId.ToString());
            phaseRecord.Status = PhasedProcessStatus.Failed;
            phaseRecord.BlueprintVersionId = blueprintVersionId;
            phaseRecord.ChildProcessId = childProcessId.ToString();
            phaseRecord.CompletedAt = failedAt;
            phaseRecord.FailureMessage = failureMessage;
            parent.Status = PhasedProcessStatus.Failed;
            parent.CurrentPhaseId = phaseId.ToString();
            parent.UpdatedAt = failedAt;

            if (await _phasedProcessRepository.TryUpdateAsync(parent, cancellationToken))
            {
                return;
            }
        }
    }

    private static Dictionary<string, JsonElement> CloneJsonMap(
        IReadOnlyDictionary<string, JsonElement> values) =>
        values.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.Clone(),
            StringComparer.Ordinal);

    private static Dictionary<Guid, Dictionary<string, JsonElement>> CloneNodeOverrides(
        IReadOnlyDictionary<Guid, Dictionary<string, JsonElement>> values) =>
        values.ToDictionary(
            entry => entry.Key,
            entry => CloneJsonMap(entry.Value),
            EqualityComparer<Guid>.Default);
}
