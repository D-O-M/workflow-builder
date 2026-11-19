using Backend.API.Infrastructure;
using Backend.Database.Relational.Entities;
using Backend.Models.InputModels;
using Microsoft.Extensions.Options;

namespace Backend.API.Validation.Blueprints;

public sealed class ConditionalBranchAuthoringValidator(IOptions<ConditionalBranchAuthoringOptions> options) : IBlueprintVersionValidator
{
    public Task<BlueprintValidationResult> ValidateAsync(BlueprintVersionInputModel model, CancellationToken ct = default)
    {
        var result = new BlueprintValidationResult();
        if (!options.Value.Enabled && model.Tasks.Any(task =>
            Enum.TryParse<BlueprintNodeKind>(task.NodeKind, ignoreCase: true, out var kind) && kind == BlueprintNodeKind.ConditionalBranch))
        {
            result.Add("feature.conditionalBranch.disabled", "Conditional Branch authoring is disabled in this environment.", "tasks");
        }
        return Task.FromResult(result);
    }
}
