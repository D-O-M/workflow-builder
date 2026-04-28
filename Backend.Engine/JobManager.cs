using System.Collections.Concurrent;
using System.Diagnostics;
using Backend.Plugins.Communication;

namespace Backend.Engine;

public class JobManager
{
    private readonly IAgentManager _agentManager;
    private readonly ConcurrentDictionary<Guid, JobContext> _jobs = new();
    private readonly ProcessLifecycleTelemetry _processLifecycleTelemetry;
    private readonly JobFinalizationService _jobFinalizationService;
    private readonly ILogger<JobManager> _logger;

    public JobManager(
        IAgentManager agentManager,
        ProcessLifecycleTelemetry processLifecycleTelemetry,
        JobFinalizationService jobFinalizationService,
        ILogger<JobManager> logger)
    {
        _agentManager = agentManager;
        _processLifecycleTelemetry = processLifecycleTelemetry;
        _jobFinalizationService = jobFinalizationService;
        _logger = logger;
    }

    public async Task<bool> StartJob(
        JobContext job,
        CancellationToken cancellationToken)
    {
        if (!_agentManager.TryAcquireJobSlot(job.ResolvedTaskVersionId))
        {
            return false;
        }

        var claimStopwatch = System.Diagnostics.Stopwatch.StartNew();
        (bool stepClaimed, bool processStarted) claim;
        try
        {
            claim = await job.TryMarkInProgressAsync(cancellationToken);
        }
        catch
        {
            _agentManager.ReleaseJobSlot(job.ResolvedTaskVersionId);
            throw;
        }

        claimStopwatch.Stop();
        _processLifecycleTelemetry.RecordStepClaim(job, claimStopwatch.Elapsed, claim.stepClaimed);
        if (!claim.stepClaimed)
        {
            _agentManager.ReleaseJobSlot(job.ResolvedTaskVersionId);
            await job.CompleteMessageAsync(cancellationToken);
            return true;
        }

        if (!_jobs.TryAdd(job.Guid, job))
        {
            _agentManager.ReleaseJobSlot(job.ResolvedTaskVersionId);
            await job.ResetToPendingAsync(cancellationToken);
            throw new InvalidOperationException($"A job with id '{job.Guid}' is already active.");
        }

        bool queued;
        var pluginQueuedAt = DateTimeOffset.UtcNow;
        var enqueueStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var previousActivity = Activity.Current;
        job.ExecutionActivity = _processLifecycleTelemetry.StartStepActivity(job);
        try
        {
            queued = await _agentManager.GetAgent(job.ResolvedTaskVersionId)
                .QueueJob(
                    job.ToInvocationEnvelope(
                        InvocationTelemetryContext.FromActivity(job.ExecutionActivity ?? Activity.Current)),
                    cancellationToken);
        }
        catch
        {
            _jobs.TryRemove(job.Guid, out _);
            _agentManager.ReleaseJobSlot(job.ResolvedTaskVersionId);
            await job.ResetToPendingAsync(cancellationToken);
            _processLifecycleTelemetry.StopStepActivity(job, result: null, exception: null);
            throw;
        }
        finally
        {
            enqueueStopwatch.Stop();
            Activity.Current = previousActivity;
        }

        _processLifecycleTelemetry.RecordAgentEnqueue(job, enqueueStopwatch.Elapsed, queued);

        if (!queued)
        {
            _jobs.TryRemove(job.Guid, out _);
            _agentManager.ReleaseJobSlot(job.ResolvedTaskVersionId);
            await job.ResetToPendingAsync(cancellationToken);
            _processLifecycleTelemetry.StopStepActivity(job, result: null, exception: null);
            return false;
        }

        job.PluginQueuedAt = pluginQueuedAt;

        previousActivity = Activity.Current;
        Activity.Current = job.ExecutionActivity ?? previousActivity;
        try
        {
            _processLifecycleTelemetry.LogStepStarted(
                job,
                job.StartedAt ?? DateTimeOffset.UtcNow);

            if (claim.processStarted)
            {
                _processLifecycleTelemetry.LogProcessStarted(
                    job,
                    job.StartedAt ?? DateTimeOffset.UtcNow);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Queued process step {BlueprintNodeId} for process {ProcessId} but failed to mark it in progress.",
                job.BlueprintNodeId,
                job.ProcessId);
        }
        finally
        {
            Activity.Current = previousActivity;
        }

        return true;
    }

    public async Task CompleteJob(
        PluginExecutionResult executionResult,
        CancellationToken cancellationToken)
    {
        if (!_jobs.TryGetValue(executionResult.JobId, out var job))
        {
            _logger.LogWarning(
                "Received a completion result for unknown job {JobId}.",
                executionResult.JobId);
            return;
        }

        var previousActivity = Activity.Current;
        Activity.Current = job.ExecutionActivity ?? previousActivity;
        Exception? completionException = null;
        try
        {
            job.StartedAt = executionResult.StartedAt;
            job.CompletedAt = executionResult.CompletedAt;
            job.OutputChannelsPopulated = [.. executionResult.OutputData.Keys];
            if (job.PluginQueuedAt is DateTimeOffset pluginQueuedAt)
            {
                _processLifecycleTelemetry.RecordPluginStartDelay(
                    job,
                    executionResult.StartedAt - pluginQueuedAt);
            }
            _processLifecycleTelemetry.RecordPluginPhaseTimings(job, executionResult.TimingsMs);
            _processLifecycleTelemetry.RecordRecordedStepDuration(job, executionResult.CompletedAt);

            var finalizationStopwatch = System.Diagnostics.Stopwatch.StartNew();
            if (executionResult.Succeeded)
            {
                await _jobFinalizationService.FinalizeSuccess(job, executionResult, cancellationToken);
            }
            else
            {
                await _jobFinalizationService.FinalizeFailureAsync(job, executionResult, cancellationToken);
            }
            finalizationStopwatch.Stop();
            _processLifecycleTelemetry.RecordFinalization(job, finalizationStopwatch.Elapsed, executionResult.Succeeded);
        }
        catch (Exception ex)
        {
            completionException = ex;
            _logger.LogError(
                ex,
                "Failed to finalize process step {BlueprintNodeId} for process {ProcessId}.",
                job.BlueprintNodeId,
                job.ProcessId);

            try
            {
                await job.AbandonMessageAsync(cancellationToken);
            }
            catch (Exception abandonEx)
            {
                _logger.LogError(
                    abandonEx,
                    "Failed to abandon queue message for process step {BlueprintNodeId} after finalization failure.",
                    job.BlueprintNodeId);
            }
        }
        finally
        {
            _processLifecycleTelemetry.StopStepActivity(job, executionResult, completionException);
            Activity.Current = previousActivity;
            _jobs.TryRemove(job.Guid, out _);
            _agentManager.ReleaseJobSlot(job.ResolvedTaskVersionId);
        }
    }
}
