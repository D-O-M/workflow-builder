using System.Text.Json;
using Backend.Interfaces.Repositories;
using Backend.Models;

namespace Backend.Engine;

internal static class NativeStepInputHydrator
{
    public static async Task<Dictionary<string, JsonElement>> HydrateInputAsync(
        IProcessStepRepository processStepRepository,
        StepExecutionContext context,
        CancellationToken cancellationToken)
    {
        var plan = context.InputHydrationPlan
            ?? throw new InvalidOperationException(
                $"Process step {context.ProcessStepId} has no input hydration plan.");
        if (!string.Equals(plan.InputChannelName, context.InputChannelName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Hydration plan targets input channel '{plan.InputChannelName}', expected '{context.InputChannelName}'.");
        }

        var partialInputs = new List<IReadOnlyDictionary<string, JsonElement>>
        {
            plan.InitialInput
        };

        foreach (var source in plan.Sources)
        {
            if (source.InlineInput is not null)
            {
                partialInputs.Add(source.InlineInput);
                continue;
            }

            var sourceStep = await processStepRepository.GetProcessStepById(
                Guid.Parse(source.SourceProcessStepId),
                context.ProcessId,
                cancellationToken);
            if (sourceStep.OutputData is null ||
                !sourceStep.OutputData.TryGetValue(source.SourceOutputChannelName, out var sourcePayload))
            {
                throw new InvalidOperationException(
                    $"Source process step '{source.SourceProcessStepId}' does not contain output channel '{source.SourceOutputChannelName}' for binding '{source.BindingId}'.");
            }

            partialInputs.Add(BlueprintInputMaterializer.MaterializePartialInput(
                sourcePayload,
                source.PropertyMappings.Select(mapping => new ExecutableBlueprintPropertyMapping
                {
                    SourceProperty = mapping.SourceProperty,
                    TargetProperty = mapping.TargetProperty
                }).ToList()));
        }

        return BlueprintInputMaterializer.MergeInputs(
            partialInputs,
            plan.DesignTimeInputOverrides,
            plan.RuntimeInputOverrides);
    }
}
