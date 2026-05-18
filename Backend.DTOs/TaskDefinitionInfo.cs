using Backend.Database.Relational.Entities;
using Backend.PluginAbi;

namespace Backend.Models;

public record TaskDefinitionInfo(
    int Id,
    int TaskDefinitionId,
    string Name,
    string Path,
    Version Version,
    TaskVersionState State,
    PluginAbiMetadata Abi);
