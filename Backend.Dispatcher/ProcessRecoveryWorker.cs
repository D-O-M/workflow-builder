using Backend.Repositories;

namespace Backend.Dispatcher;

public sealed class ProcessRecoveryWorker(
    ProcessRecoveryStore store, IServiceScopeFactory scopes, ILogger<ProcessRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            try
            {
                foreach (var batch in await store.DueBatchesAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), stoppingToken))
                    await AdvanceAsync(batch, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Failed to poll confirmed Process recoveries.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task AdvanceAsync(RecoveryBatch batch, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var cases = await store.DueCasesAsync(batch, now.ToUnixTimeMilliseconds(), ct);
        foreach (var item in cases)
        {
            var claimed = await store.ClaimAsync(item, DateTimeOffset.UtcNow, ct);
            if (claimed is null) continue;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<ProcessRecoveryExecutor>()
                    .AdvanceAsync(batch.BatchId, batch.Revision, claimed.Plan, ct);
                await store.ReplaceAsync(claimed with
                {
                    Status = result.State, Message = result.Detail,
                    NextPoll = DateTimeOffset.UtcNow.AddSeconds(3).ToUnixTimeMilliseconds()
                }, ct);
            }
            catch (EntityNotFoundException) when (!ct.IsCancellationRequested)
            {
                await store.ReplaceAsync(claimed with { Status = "Changed",
                    Message = "The Process, its selected Steps or its pinned Blueprint version is no longer available." }, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Recovery {RecoveryId} for Process {ProcessId} will resume after its lease expires.",
                    batch.BatchId, item.ProcessId);
                await store.ReplaceAsync(claimed with { Message = "Dispatcher will retry this case after its lease expires." }, ct);
            }
        }

        var unfinished = await store.HasUnfinishedCasesAsync(batch, ct);
        await store.ReplaceAsync(batch with
        {
            Status = unfinished ? "Confirmed" : "Completed",
            NextPoll = DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeMilliseconds()
        }, ct);
    }
}
