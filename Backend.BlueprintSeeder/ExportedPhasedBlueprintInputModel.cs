using Backend.Models.InputModels;

namespace Backend.BlueprintSeeder;

internal sealed class ExportedPhasedBlueprintInputModel
{
    public int SchemaVersion { get; init; }
    public string DefinitionKey { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string? Description { get; init; }
    public List<ExportedPhaseInputModel> Phases { get; init; } = [];
    public List<ExportedPhaseTransitionInputModel> Transitions { get; init; } = [];
}

internal sealed class ExportedPhaseInputModel
{
    public Guid PhaseId { get; init; }
    public int PhaseIndex { get; init; }
    public string Name { get; init; } = "";
    public string BlueprintDefinitionKey { get; init; } = "";
    public int? BlueprintVersion { get; init; }
    public string VersionPolicy { get; init; } = "Pinned";
}

internal sealed class ExportedPhaseTransitionInputModel
{
    public Guid TransitionId { get; init; }
    public Guid SourcePhaseId { get; init; }
    public Guid DestinationPhaseId { get; init; }
    public List<ExportedPhaseHandoffMappingInputModel> HandoffMappings { get; init; } = [];
    public List<PhasedBlueprintCorrelationMappingInputModel> CorrelationMappings { get; init; } = [];
}

internal sealed class ExportedPhaseHandoffMappingInputModel
{
    public string SourceKind { get; init; } = "Output";
    public Guid? SourceBlueprintNodeKey { get; init; }
    public string? SourceChannelName { get; init; }
    public string SourceProperty { get; init; } = "";
    public double BridgePosition { get; init; } = 0.5d;
    public Guid TargetBlueprintNodeKey { get; init; }
    public string TargetProperty { get; init; } = "";
}
