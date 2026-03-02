namespace Backend.Models.DomainModels;

public class TaskDefinitionDomainModel
{
    public int? Id { get; set; }
    public required string Name { get; init; }
    public required List<TaskVersionDomainModel> Versions { get; init; }
}
