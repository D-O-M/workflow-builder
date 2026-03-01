namespace Backend.DTOs.DomainModels;

public class TaskVersionDTO
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required Version Version { get; set; }
    public required string WorkspaceEntryPointPath { get; set; }
    public required HashSet<PropertyDefinitionDTO> RequiredGlobalState { get; set; }
    public ChannelDefinitionDTO? InputChannelDefinition { get; set; }
    public required HashSet<ChannelDefinitionDTO> OutputChannelDefinitions { get; set; }
}
