namespace Backend.API.Validation.Blueprints;

public sealed class BlueprintValidationResult
{
    private readonly List<BlueprintValidationError> _errors = new();

    public IReadOnlyList<BlueprintValidationError> Errors => _errors;

    public bool IsValid => _errors.Count == 0;

    public void Add(string code, string message, string? path = null)
        => _errors.Add(new(code, message, path));

    public void AddRange(IEnumerable<BlueprintValidationError> errors)
        => _errors.AddRange(errors);
}
