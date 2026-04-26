namespace Backend.Models.DomainModels;

public class TaskVersionDomainModel
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required Version Version { get; set; }
    public required string WorkspaceEntryPointPath { get; set; }
    public HashSet<PropertyDefinitionDomainModel>? RequiredGlobalState { get; set; }
    public ChannelDefinitionDomainModel? InputChannelDefinition { get; set; }
    public required HashSet<ChannelDefinitionDomainModel> OutputChannelDefinitions { get; set; }
}
