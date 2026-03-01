namespace Backend.Models.DomainModels;

public class TaskDefinitionDTO
{
    public int? Id { get; set; }
    public required string Name { get; init; }
    public required List<TaskVersionDTO> Versions { get; init; }
}
