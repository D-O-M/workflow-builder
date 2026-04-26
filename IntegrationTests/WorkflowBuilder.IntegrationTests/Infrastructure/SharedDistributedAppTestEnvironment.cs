namespace WorkflowBuilder.IntegrationTests.Infrastructure;

internal static class SharedDistributedAppTestEnvironment
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Task<DistributedAppTestEnvironment>? _startupTask;
    private static bool _disposeRequested;

    public static async Task<DistributedAppTestEnvironment> GetReadyAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, nameof(SharedDistributedAppTestEnvironment));

            _startupTask ??= DistributedAppTestEnvironment.CreateReadyAsync(cancellationToken);
        }
        finally
        {
            Gate.Release();
        }

        return await _startupTask.WaitAsync(cancellationToken);
    }

    public static async Task DisposeAsync()
    {
        Task<DistributedAppTestEnvironment>? startupTask;

        await Gate.WaitAsync();
        try
        {
            if (_disposeRequested)
            {
                return;
            }

            _disposeRequested = true;
            startupTask = _startupTask;
        }
        finally
        {
            Gate.Release();
        }

        if (startupTask is null)
        {
            return;
        }

        try
        {
            var environment = await startupTask;
            await environment.DisposeAsync();
        }
        catch
        {
            // Startup failures already include resource diagnostics. Do not mask the
            // original test failure from a best-effort session cleanup hook.
        }
    }
}
