namespace Backend.Database.Relational.Entities;

public sealed class PhasedBlueprintDefinition
{
    public int Id { get; set; }

    public string Key { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    public List<PhasedBlueprintVersion> Versions { get; set; } = new();
}

