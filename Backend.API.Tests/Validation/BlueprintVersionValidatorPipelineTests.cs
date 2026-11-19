using System;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Validation.Blueprints;
using Backend.API.Infrastructure;
using Backend.Models.InputModels;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Backend.API.Tests.Validation;

public sealed class BlueprintVersionValidatorPipelineTests
{
    private sealed class AlwaysFailValidator : IBlueprintVersionValidator
    {
        public Task<BlueprintValidationResult> ValidateAsync(BlueprintVersionInputModel model, CancellationToken ct = default)
        {
            var r = new BlueprintValidationResult();
            r.Add("fail", "nope");
            return Task.FromResult(r);
        }
    }

    private sealed class AlwaysPassValidator : IBlueprintVersionValidator
    {
        public Task<BlueprintValidationResult> ValidateAsync(BlueprintVersionInputModel model, CancellationToken ct = default)
            => Task.FromResult(new BlueprintValidationResult());
    }

    [Test]
    [Arguments(false, "ConditionalBranch", false)]
    [Arguments(true, "ConditionalBranch", true)]
    [Arguments(false, "PluginTask", true)]
    public async Task Conditional_authoring_gate_applies_only_to_branch_versions(bool enabled, string nodeKind, bool accepted)
    {
        var gate = new ConditionalBranchAuthoringValidator(Options.Create(new ConditionalBranchAuthoringOptions { Enabled = enabled }));
        var result = await gate.ValidateAsync(new BlueprintVersionInputModel { Tasks = [new() { NodeKind = nodeKind }] });
        result.IsValid.ShouldBe(accepted);
        if (!accepted)
            result.Errors[0].Code.ShouldBe("feature.conditionalBranch.disabled");
    }

    [Test]
    public async Task ValidateAsync_short_circuits_on_first_failure()
    {
        var sut = new BlueprintVersionValidatorPipeline(new IBlueprintVersionValidator[]
        {
            new AlwaysFailValidator(),
            new AlwaysPassValidator()
        });

        var model = new BlueprintVersionInputModel
        {
            BlueprintDefinitionId = 1,
            StartingBlueprintNodeId = Guid.NewGuid(),
            Tasks = new()
        };

        var result = await sut.ValidateAsync(model);

        result.IsValid.ShouldBeFalse();
        result.Errors.Count.ShouldBe(1);
        result.Errors[0].Code.ShouldBe("fail");
    }
}
