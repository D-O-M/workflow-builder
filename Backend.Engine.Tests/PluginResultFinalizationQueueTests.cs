using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Backend.Engine;
using Backend.Plugins.Communication;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Backend.Engine.Tests;

public sealed class PluginResultFinalizationQueueTests
{
    [Test]
    public async Task EnqueueAsync_waits_when_queue_is_full()
    {
        var telemetry = new PluginResultFinalizationTelemetry();
        var queue = CreateQueue(capacity: 1, telemetry);
        await queue.EnqueueAsync(CreateResult(Guid.NewGuid()), CancellationToken.None);

        var secondEnqueue = queue.EnqueueAsync(CreateResult(Guid.NewGuid()), CancellationToken.None).AsTask();
        await Task.Delay(100);

        secondEnqueue.IsCompleted.ShouldBeFalse();
        await using var reader = queue.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator();
        (await reader.MoveNextAsync()).ShouldBeTrue();
        await secondEnqueue.WaitAsync(TimeSpan.FromSeconds(1));
        queue.Depth.ShouldBe(1);
        telemetry.RecordedEnqueueWaits.ShouldBe(2);

        queue.Complete();
    }

    [Test]
    public async Task EnqueueAsync_does_not_lose_results_under_pressure()
    {
        var queue = CreateQueue(capacity: 2, new PluginResultFinalizationTelemetry());
        var expectedIds = Enumerable.Range(0, 25)
            .Select(_ => Guid.NewGuid())
            .ToArray();
        var receivedIds = new ConcurrentBag<Guid>();

        var consumer = Task.Run(async () =>
        {
            await foreach (var item in queue.ReadAllAsync(CancellationToken.None))
            {
                receivedIds.Add(item.Result.JobId);
                await Task.Delay(5);
            }
        });

        await Task.WhenAll(expectedIds
            .Select(id => queue.EnqueueAsync(CreateResult(id), CancellationToken.None).AsTask()));
        queue.Complete();
        await consumer.WaitAsync(TimeSpan.FromSeconds(5));

        receivedIds.Count.ShouldBe(expectedIds.Length);
        receivedIds.OrderBy(id => id).ShouldBe(expectedIds.OrderBy(id => id));
        queue.Depth.ShouldBe(0);
    }

    [Test]
    public void Constructor_rejects_invalid_capacity()
    {
        var exception = Should.Throw<InvalidOperationException>(() =>
            CreateQueue(capacity: 0, new PluginResultFinalizationTelemetry()));

        exception.Message.ShouldContain("ResultFinalizationQueueCapacity");
    }

    [Test]
    public void Constructor_rejects_lossy_full_modes()
    {
        var exception = Should.Throw<InvalidOperationException>(() =>
            new PluginResultFinalizationQueue(
                new PluginRuntimeOptions
                {
                    ResultFinalizationQueueCapacity = 1,
                    ResultFinalizationQueueFullMode = BoundedChannelFullMode.DropOldest
                },
                new PluginResultFinalizationTelemetry()));

        exception.Message.ShouldContain("ResultFinalizationQueueFullMode");
    }

    private static PluginResultFinalizationQueue CreateQueue(
        int capacity,
        PluginResultFinalizationTelemetry telemetry) =>
        new(
            new PluginRuntimeOptions
            {
                ResultFinalizationQueueCapacity = capacity,
                ResultFinalizationQueueFullMode = BoundedChannelFullMode.Wait
            },
            telemetry);

    private static PluginExecutionResult CreateResult(Guid jobId) =>
        new(
            jobId,
            Succeeded: true,
            EffectiveInputData: null,
            OutputData: new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            StartedAt: DateTimeOffset.UtcNow,
            CompletedAt: DateTimeOffset.UtcNow,
            TimingsMs: null,
            ErrorMessage: null);
}

public sealed class PluginResultFinalizationWorkerTests
{
    [Test]
    public async Task StopAsync_drains_queued_results()
    {
        var telemetry = new PluginResultFinalizationTelemetry();
        var queue = CreateQueue(capacity: 10, telemetry);
        var executionService = new FakeStepExecutionService();
        var worker = CreateWorker(queue, executionService, telemetry, maxConcurrency: 2);

        await worker.StartAsync(CancellationToken.None);
        var expectedIds = Enumerable.Range(0, 5)
            .Select(_ => Guid.NewGuid())
            .ToArray();
        foreach (var id in expectedIds)
        {
            await queue.EnqueueAsync(CreateResult(id), CancellationToken.None);
        }

        await worker.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        executionService.HandledIds.OrderBy(id => id).ShouldBe(expectedIds.OrderBy(id => id));
        telemetry.RecordedFinalizations.ShouldBe(expectedIds.Length);
        telemetry.RecordedFailures.ShouldBe(0);
    }

    [Test]
    public async Task Finalization_failure_is_observed_and_does_not_stop_other_results()
    {
        var telemetry = new PluginResultFinalizationTelemetry();
        var queue = CreateQueue(capacity: 10, telemetry);
        var failingId = Guid.NewGuid();
        var succeedingId = Guid.NewGuid();
        var executionService = new FakeStepExecutionService
        {
            OnHandleAsync = (result, _) =>
                result.JobId == failingId
                    ? Task.FromException(new InvalidOperationException("boom"))
                    : Task.CompletedTask
        };
        var worker = CreateWorker(queue, executionService, telemetry, maxConcurrency: 1);

        await worker.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(CreateResult(failingId), CancellationToken.None);
        await queue.EnqueueAsync(CreateResult(succeedingId), CancellationToken.None);
        await worker.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        executionService.HandledIds.ShouldContain(failingId);
        executionService.HandledIds.ShouldContain(succeedingId);
        telemetry.RecordedFinalizations.ShouldBe(2);
        telemetry.RecordedFailures.ShouldBe(1);
    }

    [Test]
    public async Task StopAsync_cancels_inflight_finalization_when_shutdown_token_expires()
    {
        var telemetry = new PluginResultFinalizationTelemetry();
        var queue = CreateQueue(capacity: 10, telemetry);
        var finalizationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalizationCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionService = new FakeStepExecutionService
        {
            OnHandleAsync = async (_, cancellationToken) =>
            {
                finalizationStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    finalizationCanceled.TrySetResult();
                    throw;
                }
            }
        };
        var worker = CreateWorker(queue, executionService, telemetry, maxConcurrency: 1);

        await worker.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(CreateResult(Guid.NewGuid()), CancellationToken.None);
        await finalizationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await worker.StopAsync(new CancellationTokenSource(TimeSpan.FromMilliseconds(50)).Token);

        await finalizationCanceled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static PluginResultFinalizationWorker CreateWorker(
        PluginResultFinalizationQueue queue,
        FakeStepExecutionService executionService,
        PluginResultFinalizationTelemetry telemetry,
        int maxConcurrency) =>
        new(
            executionService,
            telemetry,
            NullLogger<PluginResultFinalizationWorker>.Instance,
            queue,
            new PluginRuntimeOptions
            {
                MaxConcurrentResultFinalizations = maxConcurrency,
                ResultFinalizationQueueCapacity = 10
            });

    private static PluginResultFinalizationQueue CreateQueue(
        int capacity,
        PluginResultFinalizationTelemetry telemetry) =>
        new(
            new PluginRuntimeOptions
            {
                ResultFinalizationQueueCapacity = capacity,
                ResultFinalizationQueueFullMode = BoundedChannelFullMode.Wait
            },
            telemetry);

    private static PluginExecutionResult CreateResult(Guid jobId) =>
        new(
            jobId,
            Succeeded: true,
            EffectiveInputData: null,
            OutputData: new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            StartedAt: DateTimeOffset.UtcNow,
            CompletedAt: DateTimeOffset.UtcNow,
            TimingsMs: null,
            ErrorMessage: null);

    private sealed class FakeStepExecutionService : IStepExecutionService
    {
        private readonly ConcurrentBag<Guid> _handledIds = [];

        public IReadOnlyCollection<Guid> HandledIds => _handledIds;

        public Func<PluginExecutionResult, CancellationToken, Task>? OnHandleAsync { get; init; }

        public Task<StepStartResult> StartPluginStepAsync(
            StepExecutionContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(StepStartResult.Started);

        public async Task HandlePluginResultAsync(
            PluginExecutionResult executionResult,
            CancellationToken cancellationToken)
        {
            _handledIds.Add(executionResult.JobId);
            if (OnHandleAsync is not null)
            {
                await OnHandleAsync(executionResult, cancellationToken);
            }
        }
    }
}
