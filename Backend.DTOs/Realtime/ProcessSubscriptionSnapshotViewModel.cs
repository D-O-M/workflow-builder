namespace Backend.Models.Realtime;

public sealed class ProcessSubscriptionSnapshotViewModel
{
    public List<int> BlueprintDefinitionIds { get; init; } = [];
    public List<Guid> ProcessIds { get; init; } = [];
}
