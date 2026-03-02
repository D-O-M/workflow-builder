using Backend.Database.Relational.Entities;
using Backend.Models.ViewModels;

namespace Backend.Models.Mappers;

public class TaskViewModelMapper
{
    public TaskDefinitionViewModel MapViewModel(TaskDefinition taskDefinition) =>
        new TaskDefinitionViewModel
        {
            Id = taskDefinition.Id,
            Name = taskDefinition.Name,
            Versions = [..taskDefinition.Versions.Select(MapViewModel)]
        };

    public TaskVersionViewModel MapViewModel(TaskVersion taskVersion)
    {
        var viewModel = new TaskVersionViewModel
        {
            Id = taskVersion.Id,
            Version = taskVersion.Version.ToString(),
            Description = taskVersion.Description,
            RequiredGlobalState = taskVersion.GlobalState is not null
                ? MapViewModel(taskVersion.GlobalState)
                : null,
            InputChannel = taskVersion.InputChannels.Any() ?
                MapViewModel(taskVersion.InputChannels.First()) :
                null,
            OutputChannels = [..taskVersion.OutputChannels.Select(MapViewModel)]
        };
        return viewModel;
    }

    public TaskChannel MapViewModel(Database.Relational.Entities.Channel channel) =>
        new TaskChannel
        {
            Name = channel.Name,
            Properties = channel.Properties.ToDictionary(
                prop => prop.Name,
                prop => prop.Type.ToString())
        };
}
