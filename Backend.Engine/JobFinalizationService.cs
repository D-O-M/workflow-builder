using Backend.Plugins.Communication;
using Backend.Repositories;

namespace Backend.Engine;

public class JobFinalizationService
{
    private readonly NodeActivationService _nodeActivationService;
    private readonly PhasedProcessFinalizationService _phasedProcessFinalizationService;
    private readonly ProcessLifecycleTelemetry _processLifecycleTelemetry;
    private readonly ILogger<JobFinalizationService> _logger;

    public JobFinalizationService(
        NodeActivationService nodeActivationService,
        PhasedProcessFinalizationService phasedProcessFinalizationService,
        ProcessLifecycleTelemetry processLifecycleTelemetry,
        ILogger<JobFinalizationService> logger)
    {
        _nodeActivationService = nodeActivationService;
        _phasedProcessFinalizationService = phasedProcessFinalizationService;
        _processLifecycleTelemetry = processLifecycleTelemetry;
        _logger = logger;
    }

    public async Task FinalizeSuccess(
        JobContext context,
        PluginExecutionResult executionResult,
        CancellationToken cancellationToken)
    {
        var processCompleted = await context.PersistSuccessAsync(
            executionResult.EffectiveInputData,
            executionResult.OutputData,
            executionResult.GlobalState,
            executionResult.CompletedAt,
            cancellationToken);

        await _nodeActivationService.EnqueueReadyDownstreamStepsAsync(
            context,
            executionResult,
            cancellationToken);
        _processLifecycleTelemetry.LogStepCompleted(context, executionResult);

        if (processCompleted)
        {
            if (context.PhaseMetadata is not null)
            {
                await _phasedProcessFinalizationService.FinalizeCompletedPhaseAsync(
                    context.PhaseMetadata,
                    context.ExecutableBlueprintId,
                    context.ProcessId,
                    executionResult.CompletedAt,
                    cancellationToken);
            }

            _processLifecycleTelemetry.LogProcessCompleted(
                context,
                executionResult.CompletedAt);
        }

        await context.CompleteMessageAsync(cancellationToken);
    }

    public async Task FinalizeFailureAsync(
        JobContext context,
        PluginExecutionResult executionResult,
        CancellationToken cancellationToken)
    {
        await context.PersistFailureAsync(
            executionResult.EffectiveInputData,
            executionResult.GlobalState,
            executionResult.ErrorMessage,
            executionResult.CompletedAt,
            cancellationToken);
        if (context.PhaseMetadata is not null)
        {
            await _phasedProcessFinalizationService.MarkPhaseFailedAsync(
                context.PhaseMetadata,
                context.ExecutableBlueprintId,
                context.ProcessId,
                executionResult.ErrorMessage,
                executionResult.CompletedAt,
                cancellationToken);
        }

        _processLifecycleTelemetry.LogStepFailed(context, executionResult);
        _processLifecycleTelemetry.LogProcessFailed(
            context,
            executionResult.CompletedAt,
            executionResult.ErrorMessage);
        await context.CompleteMessageAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(executionResult.ErrorMessage))
        {
            _logger.LogError(
                "Process step {BlueprintNodeId} for process {ProcessId} failed: {ErrorMessage}",
                context.BlueprintNodeId,
                context.ProcessId,
                executionResult.ErrorMessage);
        }
    }
}
