namespace Backend.Models;

public sealed class BlueprintImportValidationException : Exception
{
    public BlueprintImportValidationException(IEnumerable<BlueprintImportValidationError> errors)
        : base("Blueprint import validation failed.")
    {
        Errors = errors.ToList().AsReadOnly();
    }

    public IReadOnlyList<BlueprintImportValidationError> Errors { get; }
}
