namespace Backend.Models;

public sealed record BlueprintImportValidationError(
    string Code,
    string Message,
    string? Path = null);
