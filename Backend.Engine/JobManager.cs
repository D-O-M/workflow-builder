using Backend.Plugins.Communication;

namespace Backend.Engine;

public class JobManager
{
    private readonly IAgentManager _agentManager;
    private readonly Dictionary<Guid, JobContext> _jobs = new();
    private readonly JobFinalizationService _jobFinalizationService;
    private readonly ILogger<JobManager> _logger;

    public JobManager(
        IAgentManager agentManager,
        JobFinalizationService jobFinalizationService,
        ILogger<JobManager> logger)
    {
        _agentManager = agentManager;
        _jobFinalizationService = jobFinalizationService;
        _logger = logger;
    }

    public async Task<bool> StartJob(
        JobContext job,
        CancellationToken cancellationToken)
    {
        _jobs.Add(job.Guid, job);

        var queued = await _agentManager.GetAgent(job.SchemaId)
            .QueueJob(job.ToInvocationEnvelope(), cancellationToken);

        if (!queued)
        {
            _jobs.Remove(job.Guid);
            return false;
        }

        try
        {
            await job.MarkInProgressAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Queued workflow task {BlueprintTaskId} for workflow instance {WorkflowInstanceId} but failed to mark it in progress.",
                job.BlueprintTaskId,
                job.WorkflowInstanceId);
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

        job.StartedAt = executionResult.StartedAt;
        job.CompletedAt = executionResult.CompletedAt;
        job.OutputChannelsPopulated = [.. executionResult.OutputData.Keys];

        try
        {
            if (executionResult.Succeeded)
            {
                await _jobFinalizationService.FinalizeSuccess(job, executionResult, cancellationToken);
            }
            else
            {
                await _jobFinalizationService.FinalizeFailureAsync(job, executionResult, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to finalize workflow task {BlueprintTaskId} for workflow instance {WorkflowInstanceId}.",
                job.BlueprintTaskId,
                job.WorkflowInstanceId);

            try
            {
                await job.AbandonMessageAsync(cancellationToken);
            }
            catch (Exception abandonEx)
            {
                _logger.LogError(
                    abandonEx,
                    "Failed to abandon queue message for workflow task {BlueprintTaskId} after finalization failure.",
                    job.BlueprintTaskId);
            }
        }
        finally
        {
            _jobs.Remove(job.Guid);
        }
    }
}
