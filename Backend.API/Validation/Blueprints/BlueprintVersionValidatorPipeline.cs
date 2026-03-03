using Backend.Models.InputModels;

namespace Backend.API.Validation.Blueprints;

public sealed class BlueprintVersionValidatorPipeline : IBlueprintVersionValidator
{
    private readonly IReadOnlyList<IBlueprintVersionValidator> _validators;

    public BlueprintVersionValidatorPipeline(IEnumerable<IBlueprintVersionValidator> validators)
        => _validators = validators.ToList();

    public async Task<BlueprintValidationResult> ValidateAsync(BlueprintVersionInputModel model, CancellationToken ct = default)
    {
        var result = new BlueprintValidationResult();

        foreach (var validator in _validators)
        {
            ct.ThrowIfCancellationRequested();
            var r = await validator.ValidateAsync(model, ct);
            result.AddRange(r.Errors);
            if (!result.IsValid)
                return result;
        }

        return result;
    }
}
