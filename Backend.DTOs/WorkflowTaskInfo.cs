using Backend.Database.Relational.Entities;

namespace Backend.Models;

public record WorkflowTaskInfo(
    int Id,
    int TaskDefinitionId,
    string Name,
    string Path,
    Version Version,
    TaskVersionState State);
