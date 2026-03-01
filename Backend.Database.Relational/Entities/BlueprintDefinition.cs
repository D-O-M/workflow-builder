namespace Backend.Database.Relational.Entities;

public class BlueprintDefinition
{
    public int Id { get; set; }
    public string Key { get; set; } = null!;

    public List<BlueprintVersion> Versions { get; set; } = new();
}
