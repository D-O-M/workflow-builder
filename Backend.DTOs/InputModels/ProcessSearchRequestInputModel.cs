namespace Backend.Models.InputModels;

public sealed class ProcessSearchRequestInputModel
{
    public List<int> BlueprintDefinitionIds { get; init; } = [];
    public List<ProcessSearchBlueprintFilterInputModel> BlueprintFilters { get; init; } = [];
    public List<string> ProcessStatuses { get; init; } = [];
    public DateTimeOffset CreatedFrom { get; init; }
    public DateTimeOffset CreatedTo { get; init; }
    public List<ProcessSearchNodeFilterInputModel> NodeFilters { get; init; } = [];
    public List<ProcessSearchPropertyFilterInputModel> PropertyFilters { get; init; } = [];
    public int Limit { get; init; } = 25;
    public string? Cursor { get; init; }
}

public sealed class ProcessSearchBlueprintFilterInputModel
{
    public int BlueprintDefinitionId { get; init; }
    public List<string> Statuses { get; init; } = [];
}

public sealed class ProcessSearchNodeFilterInputModel
{
    public int BlueprintDefinitionId { get; init; }
    public Guid BlueprintNodeId { get; init; }
    public List<string> Statuses { get; init; } = [];
}

public sealed class ProcessSearchPropertyFilterInputModel
{
    public string Scope { get; init; } = string.Empty;
    public int? BlueprintDefinitionId { get; init; }
    public Guid? BlueprintNodeId { get; init; }
    public string PropertyKey { get; init; } = string.Empty;
    public string PropertyType { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}
