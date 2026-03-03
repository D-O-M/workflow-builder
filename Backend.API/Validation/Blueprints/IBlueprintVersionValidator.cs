using Backend.Models.InputModels;

namespace Backend.API.Validation.Blueprints;

public interface IBlueprintVersionValidator
{
    Task<BlueprintValidationResult> ValidateAsync(BlueprintVersionInputModel model, CancellationToken ct = default);
}
