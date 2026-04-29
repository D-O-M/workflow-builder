using System.Text.Json;
using Backend.Database.Relational.Entities;
using Backend.Models;
using Backend.Models.ViewModels;

namespace Backend.Models.Mappers;

public class TaskViewModelMapper
{
    public TaskDefinitionViewModel MapViewModel(TaskDefinition taskDefinition) =>
        new TaskDefinitionViewModel
        {
            Id = taskDefinition.Id,
            Name = taskDefinition.Name,
            DisplayName = NormalizeDisplayName(taskDefinition.DisplayName, taskDefinition.Name),
            Versions = [..taskDefinition.Versions
                .OrderByDescending(version => version.Version)
                .Select(MapViewModel)]
        };

    public TaskVersionViewModel MapViewModel(TaskVersion taskVersion)
    {
        var viewModel = new TaskVersionViewModel
        {
            Id = taskVersion.Id,
            Version = taskVersion.Version.ToString(),
            State = TaskVersionState.Registered.ToString(),
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

    private static string NormalizeDisplayName(string? displayName, string fallbackName) =>
        string.IsNullOrWhiteSpace(displayName)
            ? fallbackName
            : displayName.Trim();

    public TaskChannel MapViewModel(Database.Relational.Entities.Channel channel) =>
        new TaskChannel
        {
            Id = channel.Id,
            Name = channel.Name,
            Properties = [..channel.Properties
                .OrderBy(prop => prop.Name, StringComparer.Ordinal)
                .Select(prop => new TaskPropertyViewModel
                {
                    Name = prop.Name,
                    Kind = WorkflowPropertyTypeName.Format(prop.Kind, prop.IsCollection),
                    Required = prop.IsRequired,
                    EnumValues = DeserializeEnumValues(prop.EnumValuesJson)
                })]
        };

    private static List<string> DeserializeEnumValues(string? enumValuesJson)
    {
        if (string.IsNullOrWhiteSpace(enumValuesJson))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<string>>(enumValuesJson) ?? [];
    }
}
