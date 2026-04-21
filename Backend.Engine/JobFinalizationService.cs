using Backend.Plugins.Communication;

namespace Backend.Engine;

public class JobFinalizationService
{
    private readonly NodeActivationService _nodeActivationService;
    private readonly ProcessLifecycleTelemetry _processLifecycleTelemetry;
    private readonly ILogger<JobFinalizationService> _logger;

    public JobFinalizationService(
        NodeActivationService nodeActivationService,
        ProcessLifecycleTelemetry processLifecycleTelemetry,
        ILogger<JobFinalizationService> logger)
    {
        _nodeActivationService = nodeActivationService;
        _processLifecycleTelemetry = processLifecycleTelemetry;
        _logger = logger;
    }

    public async Task FinalizeSuccess(
        JobContext context,
        PluginExecutionResult executionResult,
        CancellationToken cancellationToken)
    {
        await context.PersistSuccessAsync(
            executionResult.OutputData,
            executionResult.GlobalState,
            executionResult.CompletedAt,
            cancellationToken);

        await _nodeActivationService.EnqueueReadyDownstreamStepsAsync(
            context,
            executionResult,
            cancellationToken);
        _processLifecycleTelemetry.LogStepCompleted(context, executionResult);

        if (await context.CompleteProcessIfTerminalAsync(cancellationToken))
        {
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
            executionResult.GlobalState,
            executionResult.ErrorMessage,
            executionResult.CompletedAt,
            cancellationToken);
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
