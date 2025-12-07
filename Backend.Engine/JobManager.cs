namespace Backend.Engine;

public class JobManager
{
    private readonly IAgentManager _agentManager;
    private readonly Dictionary<Guid, JobContext> _jobs = new();
    private readonly JobFinalizationService _jobFinalizationService;

    public JobManager(
        IAgentManager agentManager,
        JobFinalizationService jobFinalizationService)
    {
        _agentManager = agentManager;
        _jobFinalizationService = jobFinalizationService;
    }

    public async Task<bool> StartJob(
        JobContext job,
        CancellationToken cancellationToken)
    {
        _jobs.Add(job.Guid, job);
        await _agentManager.GetAgent(job.Job.WorkflowTaskId)
            .QueueJob(
                new(job.WorkflowInstanceId,
                    job.WorkflowTaskInstanceId,
                    job.PreviousTaskInstanceId,
                    job.ProcessingWindowExpiresAt),
                cancellationToken);

        return true;
    }

    public async Task CompleteJob(
        JobCompletionEvent jobCompletionEvent,
        CancellationToken cancellationToken)
    {
        var job = _jobs[jobCompletionEvent.JobId];
        job.StartedAt = jobCompletionEvent.StartedAt;
        job.CompletedAt = jobCompletionEvent.CompletedAt;
        job.OutputChannelsPopulated = jobCompletionEvent.OutputChannelsPopulated;
        await _jobFinalizationService.FinalizeSuccess(job, cancellationToken);
        _jobs.Remove(job.Guid);
    }
}
