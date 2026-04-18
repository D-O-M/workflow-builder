namespace Backend.Models.DomainModels;

public sealed record TaskVersionRegistrationResult(
    int TaskVersionId,
    int TaskDefinitionId,
    string TaskName,
    Version Version,
    PluginVersionChangeKind ChangeKind);
