namespace Backend.API.Validation.Blueprints;

public sealed record BlueprintValidationError(
    string Code,
    string Message,
    string? Path = null);
