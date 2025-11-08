namespace Backend.DTOs;

public record WorkflowTaskInfo(
    int Id,
    string Name,
    string Path,
    Version Version);
