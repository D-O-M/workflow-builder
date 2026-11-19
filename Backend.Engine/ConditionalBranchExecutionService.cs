using System.Diagnostics;
using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Models;
using Backend.Plugins.Communication;

namespace Backend.Engine;

public sealed class ConditionalBranchExecutionService(
    StepClaimService claimService,
    IProcessStepRepository steps,
    IProcessStepFinalizationRepository checkpoints,
    IStepFinalizationService finalization,
    ProcessLifecycleTelemetry telemetry)
{
    public async Task ExecuteAsync(StepExecutionContext context, CancellationToken cancellationToken)
    {
        var claim = await claimService.TryClaimStepAsync(context, cancellationToken);
        if (!claim.StepClaimed)
        {
            var step = await claimService.GetPersistedStepAsync(context, cancellationToken);
            if (step.Status != ProcessStepStatus.InProgress)
            {
                await context.CompleteMessageAsync(cancellationToken);
                return;
            }
            var checkpoint = await checkpoints.GetAsync(context.ProcessStepId, cancellationToken);
            if (checkpoint is not null)
            {
                await finalization.ResumeStepFinalizationAsync(context, checkpoint, cancellationToken);
                return;
            }
            // A previous worker may have stopped after claiming this Step. Evaluation
            // is pure and CreateIfNotExists selects one durable decision.
            await claimService.HydrateStepForFinalizationAsync(context, cancellationToken);
        }

        var startedAt = context.StartedAt ?? DateTimeOffset.UtcNow;
        var previousActivity = Activity.Current;
        context.ExecutionActivity = telemetry.StartStepActivity(context);
        PluginExecutionResult? result = null;
        Exception? failure = null;
        try
        {
            telemetry.LogStepStarted(context, startedAt);
            if (claim.ProcessStarted)
            {
                telemetry.LogProcessStarted(context, startedAt);
            }

            var fields = await NativeStepInputHydrator.HydrateInputAsync(steps, context, cancellationToken);
            var input = BlueprintInputMaterializer.BuildChannelPayload(context.InputChannelName, fields);
            var output = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            try
            {
                var decision = ConditionalRuleEngine.Evaluate(
                    context.BlueprintNode.ConditionalRule, context.BlueprintNode.ConditionalFields, fields);
                output.Add(decision ? "True" : "False", input[context.InputChannelName]);
            }
            catch (InvalidDataException ex)
            {
                failure = ex;
            }

            result = new PluginExecutionResult(context.ProcessStepId, failure is null, input, output,
                startedAt, DateTimeOffset.UtcNow, TimingsMs: null, ErrorMessage: failure?.Message);
            telemetry.RecordRecordedStepDuration(context, result.CompletedAt);
            if (result.Succeeded)
            {
                await finalization.FinalizeSucceededStepAsync(context, result, cancellationToken);
            }
            else
            {
                await finalization.FinalizeFailedStepAsync(context, result, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            telemetry.StopStepActivity(context, result, failure);
            Activity.Current = previousActivity;
        }
    }
}
