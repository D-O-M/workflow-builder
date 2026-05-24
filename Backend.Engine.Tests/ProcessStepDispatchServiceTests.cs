using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.Database.State.Objects;
using Backend.Interfaces.Queues;
using Backend.Interfaces.Repositories;
using Backend.Models;
using Backend.Plugins.Communication;
using Backend.Services.ProcessStepQueue;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Backend.Engine.Tests;

public sealed class ProcessStepDispatchServiceTests
{
    [Test]
    public async Task EnsureDispatchIntent_creates_active_intent_projection()
    {
        var repository = new InMemoryProcessStepRepository();
        var dispatchIntentRepository = new InMemoryProcessStepDispatchIntentRepository();
        var queueWriter = new ProcessStepQueueWriterSpy();
        var service = CreateService(repository, dispatchIntentRepository, queueWriter);
        var step = CreateStep();
        repository.States[Guid.Parse(step.Id)] = step;

        var intent = await service.EnsureDispatchIntentAsync(step, step.CreatedAt, CancellationToken.None);

        step.Dispatch.ShouldNotBeNull();
        step.Dispatch.ActiveIntentId.ShouldBe(intent.Id);
        step.Dispatch.DueBucket.ShouldBe(intent.DueBucket);
        step.Dispatch.Shard.ShouldBe(intent.Shard);
        dispatchIntentRepository.Intents.Single().Id.ShouldBe(intent.Id);
        queueWriter.Messages.Count.ShouldBe(0);
    }

    [Test]
    public async Task Dispatch_failure_keeps_intent_retryable_and_worker_later_sends()
    {
        var repository = new InMemoryProcessStepRepository();
        var dispatchIntentRepository = new InMemoryProcessStepDispatchIntentRepository();
        var queueWriter = new ProcessStepQueueWriterSpy { ThrowOnEnqueueAttempts = 1 };
        var service = CreateService(repository, dispatchIntentRepository, queueWriter);
        var step = CreateStep();
        repository.States[Guid.Parse(step.Id)] = step;

        var firstAttempt = await service.TryDispatchAsync(step, CancellationToken.None);

        firstAttempt.ShouldBeFalse();
        queueWriter.Messages.Count.ShouldBe(0);
        step.Dispatch.ShouldNotBeNull();
        step.Dispatch.Status.ShouldBe(ProcessStepDispatchStatus.RetryableDispatchError);
        step.Dispatch.ActiveIntentId.ShouldNotBeNull();
        dispatchIntentRepository.Intents.Count.ShouldBe(2);
        var retryIntent = dispatchIntentRepository.Intents.Single(intent => intent.Id == step.Dispatch.ActiveIntentId);
        retryIntent.DueBucket.ShouldBeGreaterThan(ProcessStepDispatchService.CalculateDueBucket(
            retryIntent.CreatedAt,
            TimeSpan.FromMilliseconds(1)));

        queueWriter.ThrowOnEnqueueAttempts = 0;
        await Task.Delay(20);
        var dispatchedCount = await service.DrainDueDispatchesAsync(10, CancellationToken.None);

        dispatchedCount.ShouldBe(1);
        queueWriter.Messages.Count.ShouldBe(1);
        queueWriter.Messages[0].ProcessStepId.ShouldBe(Guid.Parse(step.Id));
        step.Dispatch.Status.ShouldBe(ProcessStepDispatchStatus.Dispatched);
        dispatchIntentRepository.Intents.Count(intent => intent.Status == ProcessStepDispatchStatus.Dispatched).ShouldBe(1);
    }

    [Test]
    public async Task Duplicate_worker_execution_claims_dispatch_once()
    {
        var repository = new InMemoryProcessStepRepository();
        var dispatchIntentRepository = new InMemoryProcessStepDispatchIntentRepository();
        var queueWriter = new ProcessStepQueueWriterSpy();
        var service = CreateService(repository, dispatchIntentRepository, queueWriter);
        var step = CreateStep();
        repository.States[Guid.Parse(step.Id)] = step;

        var results = await Task.WhenAll(
            service.TryDispatchAsync(step, CancellationToken.None),
            service.TryDispatchAsync(step, CancellationToken.None));

        results.Count(result => result).ShouldBe(1);
        queueWriter.Messages.Count.ShouldBe(1);
        step.Dispatch.ShouldNotBeNull();
        step.Dispatch.AttemptCount.ShouldBe(1);
        step.Dispatch.Status.ShouldBe(ProcessStepDispatchStatus.Dispatched);
        dispatchIntentRepository.Intents.Count.ShouldBe(1);
    }

    [Test]
    public async Task Immediate_dispatch_uses_known_step_without_reloading_process_step()
    {
        var repository = new InMemoryProcessStepRepository();
        var dispatchIntentRepository = new InMemoryProcessStepDispatchIntentRepository();
        var queueWriter = new ProcessStepQueueWriterSpy();
        var service = CreateService(repository, dispatchIntentRepository, queueWriter);
        var step = CreateStep();
        repository.States[Guid.Parse(step.Id)] = step;

        var intent = await service.EnsureDispatchIntentAsync(step, step.CreatedAt, CancellationToken.None);
        repository.GetProcessStepByIdCalls = 0;

        var dispatched = await service.TryDispatchAsync(step, intent, CancellationToken.None);

        dispatched.ShouldBeTrue();
        queueWriter.Messages.Count.ShouldBe(1);
        repository.GetProcessStepByIdCalls.ShouldBe(0);
    }

    [Test]
    public async Task Drain_due_dispatches_throttles_cross_partition_backlog_metrics()
    {
        var repository = new InMemoryProcessStepRepository();
        var dispatchIntentRepository = new InMemoryProcessStepDispatchIntentRepository();
        var queueWriter = new ProcessStepQueueWriterSpy();
        var service = CreateService(repository, dispatchIntentRepository, queueWriter);
        var step = CreateStep();
        repository.States[Guid.Parse(step.Id)] = step;

        await service.EnsureDispatchIntentAsync(step, step.CreatedAt, CancellationToken.None);
        await Task.Delay(20);

        await service.DrainDueDispatchesAsync(10, CancellationToken.None);
        await service.DrainDueDispatchesAsync(10, CancellationToken.None);

        dispatchIntentRepository.BacklogSnapshotCalls.ShouldBe(1);
    }

    [Test]
    public async Task Terminal_steps_are_not_dispatched()
    {
        var repository = new InMemoryProcessStepRepository();
        var dispatchIntentRepository = new InMemoryProcessStepDispatchIntentRepository();
        var queueWriter = new ProcessStepQueueWriterSpy();
        var service = CreateService(repository, dispatchIntentRepository, queueWriter);
        var step = CreateStep(ProcessStepStatus.Completed);
        repository.States[Guid.Parse(step.Id)] = step;

        var dispatchedCount = await service.DrainDueDispatchesAsync(10, CancellationToken.None);
        var directAttempt = await service.TryDispatchAsync(step, CancellationToken.None);

        dispatchedCount.ShouldBe(0);
        directAttempt.ShouldBeFalse();
        queueWriter.Messages.Count.ShouldBe(0);
        step.Dispatch.ShouldNotBeNull();
        step.Dispatch.Status.ShouldBe(ProcessStepDispatchStatus.Pending);
        dispatchIntentRepository.Intents.Count.ShouldBe(0);
    }

    [Test]
    public async Task Dispatch_is_marked_completed_only_after_queue_send_succeeds()
    {
        var repository = new InMemoryProcessStepRepository();
        var dispatchIntentRepository = new InMemoryProcessStepDispatchIntentRepository();
        var queueWriter = new ProcessStepQueueWriterSpy { ThrowOnEnqueueAttempts = 1 };
        var service = CreateService(repository, dispatchIntentRepository, queueWriter);
        var step = CreateStep();
        repository.States[Guid.Parse(step.Id)] = step;

        await service.TryDispatchAsync(step, CancellationToken.None);

        step.Dispatch.ShouldNotBeNull();
        step.Dispatch.Status.ShouldNotBe(ProcessStepDispatchStatus.Dispatched);
        step.Dispatch.DispatchedAt.ShouldBeNull();
        dispatchIntentRepository.Intents.Any(intent => intent.Status == ProcessStepDispatchStatus.Dispatched).ShouldBeFalse();
    }

    [Test]
    public async Task Stale_dispatching_intent_is_reclaimed_after_lock_expiry()
    {
        var repository = new InMemoryProcessStepRepository();
        var dispatchIntentRepository = new InMemoryProcessStepDispatchIntentRepository();
        var queueWriter = new ProcessStepQueueWriterSpy();
        var service = CreateService(repository, dispatchIntentRepository, queueWriter);
        var step = CreateStep();
        repository.States[Guid.Parse(step.Id)] = step;

        var intent = await service.EnsureDispatchIntentAsync(step, step.CreatedAt, CancellationToken.None);
        intent.Status = ProcessStepDispatchStatus.Dispatching;
        intent.LeaseId = "expired";
        intent.LockedUntil = DateTimeOffset.UtcNow.AddMilliseconds(-1);
        intent.DispatchingStartedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await Task.Delay(20);

        var dispatchedCount = await service.DrainDueDispatchesAsync(10, CancellationToken.None);

        dispatchedCount.ShouldBe(1);
        queueWriter.Messages.Count.ShouldBe(1);
        intent.Status.ShouldBe(ProcessStepDispatchStatus.Dispatched);
        step.Dispatch!.Status.ShouldBe(ProcessStepDispatchStatus.Dispatched);
    }

    [Test]
    public async Task Worker_drains_exact_due_bucket_shard_partitions_without_processstep_scan()
    {
        var repository = new InMemoryProcessStepRepository();
        var dispatchIntentRepository = new InMemoryProcessStepDispatchIntentRepository();
        var queueWriter = new ProcessStepQueueWriterSpy();
        var service = CreateService(repository, dispatchIntentRepository, queueWriter);
        var step = CreateStep();
        repository.States[Guid.Parse(step.Id)] = step;

        await service.EnsureDispatchIntentAsync(step, step.CreatedAt, CancellationToken.None);
        await Task.Delay(20);

        var dispatchedCount = await service.DrainDueDispatchesAsync(10, CancellationToken.None);

        dispatchedCount.ShouldBe(1);
        queueWriter.Messages.Count.ShouldBe(1);
        repository.DueStepDispatchScanCalls.ShouldBe(0);
        dispatchIntentRepository.DueQueries.ShouldNotBeEmpty();
        dispatchIntentRepository.DueQueries.ShouldAllBe(query => query.Shard >= 0 && query.Shard < 4);
    }

    [Test]
    public async Task Disabled_production_dispatcher_throws_instead_of_silent_noop()
    {
        var repository = new InMemoryProcessStepRepository();
        var dispatchIntentRepository = new InMemoryProcessStepDispatchIntentRepository();
        var queueWriter = new ProcessStepQueueWriterSpy();
        var service = CreateService(
            repository,
            dispatchIntentRepository,
            queueWriter,
            enabled: false);
        var step = CreateStep();
        repository.States[Guid.Parse(step.Id)] = step;

        Should.Throw<ProcessStepDispatchDisabledException>(service.EnsureDispatchAvailableForExecution);
        await Should.ThrowAsync<ProcessStepDispatchDisabledException>(() =>
            service.EnsureDispatchIntentAsync(step, step.CreatedAt, CancellationToken.None));
        await Should.ThrowAsync<ProcessStepDispatchDisabledException>(() =>
            service.TryDispatchAsync(step, CancellationToken.None));
        await Should.ThrowAsync<ProcessStepDispatchDisabledException>(() =>
            service.DrainDueDispatchesAsync(10, CancellationToken.None));

        queueWriter.Messages.Count.ShouldBe(0);
        dispatchIntentRepository.Intents.Count.ShouldBe(0);
    }

    [Test]
    public async Task Worker_fails_loudly_when_dispatch_is_disabled()
    {
        var service = CreateService(
            new InMemoryProcessStepRepository(),
            new InMemoryProcessStepDispatchIntentRepository(),
            new ProcessStepQueueWriterSpy(),
            enabled: false);
        var worker = new ProcessStepDispatchWorker(
            service,
            new ProcessStepDispatchOptions { Enabled = false },
            NullLogger<ProcessStepDispatchWorker>.Instance);

        await ((IHostedService)worker).StartAsync(CancellationToken.None);

        var executeTask = worker.ExecuteTask ??
            throw new InvalidOperationException("Expected dispatch worker to expose an execution task.");
        await Should.ThrowAsync<ProcessStepDispatchDisabledException>(async () => await executeTask);
    }

    private static ProcessStepDispatchService CreateService(
        InMemoryProcessStepRepository repository,
        InMemoryProcessStepDispatchIntentRepository dispatchIntentRepository,
        ProcessStepQueueWriterSpy queueWriter,
        bool enabled = true) =>
        new(
            repository,
            dispatchIntentRepository,
            queueWriter,
            new ProcessStepDispatchOptions
            {
                Enabled = enabled,
                RetryBaseDelay = TimeSpan.FromMilliseconds(1),
                RetryMaxDelay = TimeSpan.FromMilliseconds(10),
                LeaseDuration = TimeSpan.FromSeconds(30),
                DueBucketDuration = TimeSpan.FromMilliseconds(1),
                ShardCount = 4
            },
            new ProcessStepDispatchTelemetry(),
            NullLogger<ProcessStepDispatchService>.Instance);

    private static ProcessStepRecord CreateStep(
        ProcessStepStatus status = ProcessStepStatus.Pending)
    {
        var processId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var stepId = ProcessStepIdentity.Create(processId, nodeId);
        var now = DateTimeOffset.UtcNow;
        return new ProcessStepRecord
        {
            Id = stepId.ToString(),
            ProcessId = processId.ToString(),
            BlueprintId = "101",
            BlueprintNodeId = nodeId.ToString(),
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
            InputHydrationPlan = new InputHydrationPlan
            {
                InputChannelName = "Input"
            },
            Dispatch = ProcessStepDispatchService.CreatePendingDispatchState(now)
        };
    }

    private sealed class InMemoryProcessStepRepository : IProcessStepRepository
    {
        private readonly object _gate = new();

        public Dictionary<Guid, ProcessStepRecord> States { get; } = new();

        public int DueStepDispatchScanCalls { get; private set; }

        public int GetProcessStepByIdCalls { get; set; }

        public Task<IReadOnlyList<ProcessStepRecord>> GetDueStepDispatchesAsync(
            DateTimeOffset now,
            int maxItemCount,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                DueStepDispatchScanCalls++;
                return Task.FromResult<IReadOnlyList<ProcessStepRecord>>(States.Values
                    .Where(state => state.Status is ProcessStepStatus.Pending && IsDispatchDue(state.Dispatch, now))
                    .OrderBy(state => state.CreatedAt)
                    .Take(maxItemCount)
                    .ToList());
            }
        }

        public Task<ProcessStepDispatchBacklogSnapshot> GetDispatchBacklogSnapshotAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var pending = States.Values
                    .Where(state => state.Status is ProcessStepStatus.Pending &&
                        state.Dispatch?.Status is not ProcessStepDispatchStatus.Dispatched and not ProcessStepDispatchStatus.DispatchSkippedTerminal)
                    .OrderBy(state => state.CreatedAt)
                    .ToList();
                var oldest = pending.FirstOrDefault();
                return Task.FromResult(new ProcessStepDispatchBacklogSnapshot(
                    pending.Count,
                    oldest?.Dispatch?.CreatedAt ?? oldest?.CreatedAt));
            }
        }

        public Task<(bool Claimed, ProcessStepRecord? State)> TryClaimStepDispatchAsync(
            ProcessStepRecord state,
            string leaseId,
            DateTimeOffset now,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var processStepId = Guid.Parse(state.Id);
                if (!States.TryGetValue(processStepId, out var current) ||
                    current.Status is not ProcessStepStatus.Pending ||
                    !IsDispatchDue(current.Dispatch, now))
                {
                    return Task.FromResult<(bool, ProcessStepRecord?)>((false, null));
                }

                current.Dispatch ??= ProcessStepDispatchService.CreatePendingDispatchState(current.CreatedAt);
                current.Dispatch.Status = ProcessStepDispatchStatus.Dispatching;
                current.Dispatch.MessageId = current.Id;
                current.Dispatch.AttemptCount++;
                current.Dispatch.UpdatedAt = now;
                current.Dispatch.NextAttemptAt = null;
                current.Dispatch.DispatchingStartedAt = now;
                current.Dispatch.LockedUntil = now.Add(leaseDuration);
                current.Dispatch.LeaseId = leaseId;
                return Task.FromResult<(bool, ProcessStepRecord?)>((true, current));
            }
        }

        public Task<bool> MarkStepDispatchSucceededAsync(
            ProcessStepRecord state,
            string leaseId,
            DateTimeOffset dispatchedAt,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (!TryGetLeasedDispatch(state, leaseId, out var dispatch))
                {
                    return Task.FromResult(false);
                }

                dispatch.Status = ProcessStepDispatchStatus.Dispatched;
                dispatch.MessageId = state.Id;
                dispatch.UpdatedAt = dispatchedAt;
                dispatch.DispatchedAt = dispatchedAt;
                dispatch.NextAttemptAt = null;
                dispatch.DispatchingStartedAt = null;
                dispatch.LockedUntil = null;
                dispatch.LeaseId = null;
                dispatch.LastErrorKind = null;
                dispatch.LastErrorMessage = null;
                dispatch.LastErrorAt = null;
                return Task.FromResult(true);
            }
        }

        public Task<bool> MarkStepDispatchRetryableErrorAsync(
            ProcessStepRecord state,
            string leaseId,
            ProcessStepDispatchErrorKind errorKind,
            string errorMessage,
            DateTimeOffset failedAt,
            DateTimeOffset nextAttemptAt,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (!TryGetLeasedDispatch(state, leaseId, out var dispatch))
                {
                    return Task.FromResult(false);
                }

                dispatch.Status = ProcessStepDispatchStatus.RetryableDispatchError;
                dispatch.UpdatedAt = failedAt;
                dispatch.NextAttemptAt = nextAttemptAt;
                dispatch.DispatchingStartedAt = null;
                dispatch.LockedUntil = null;
                dispatch.LeaseId = null;
                dispatch.LastErrorKind = errorKind;
                dispatch.LastErrorMessage = errorMessage;
                dispatch.LastErrorAt = failedAt;
                return Task.FromResult(true);
            }
        }

        public Task<ProcessStepRecord> CreateProcessStep(ProcessStepRecord state, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<(bool Created, ProcessStepRecord State)> CreateProcessStepIfNotExists(
            ProcessStepRecord state,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessStepRecord> GetProcessStepById(
            Guid taskInstanceId,
            Guid processId,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                GetProcessStepByIdCalls++;
                if (States.TryGetValue(taskInstanceId, out var state) &&
                    string.Equals(state.ProcessId, processId.ToString(), StringComparison.Ordinal))
                {
                    return Task.FromResult(state);
                }

                throw new KeyNotFoundException($"Step {taskInstanceId} was not found.");
            }
        }

        public Task<(bool Claimed, ProcessStepRecord? State)> TryClaimProcessStepAsync(
            Guid taskInstanceId,
            Guid processId,
            int taskDefinitionId,
            int allowedMajor,
            int allowedMinor,
            int resolvedTaskVersionId,
            string resolvedTaskVersion,
            DateTimeOffset startedAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<List<ProcessStepRecord>> GetProcessStepsForProcess(
            Guid processId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CompleteProcessStepAsync(
            Guid taskInstanceId,
            Guid processId,
            Dictionary<string, JsonElement>? effectiveInputData,
            Dictionary<string, JsonElement> outputData,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task FailProcessStepAsync(
            Guid taskInstanceId,
            Guid processId,
            Dictionary<string, JsonElement>? effectiveInputData,
            string? errorMessage,
            DateTimeOffset failedAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessStepRecord> UpdateProcessStep(ProcessStepRecord state, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryUpdateProcessStep(ProcessStepRecord state, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryUpdateStepDispatchAsync(
            Guid processStepId,
            Guid processId,
            ProcessStepDispatchState dispatch,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (!States.TryGetValue(processStepId, out var state) ||
                    !string.Equals(state.ProcessId, processId.ToString(), StringComparison.Ordinal) ||
                    state.Status is not ProcessStepStatus.Pending)
                {
                    return Task.FromResult(false);
                }

                state.Dispatch = dispatch;
                state.UpdatedAt = dispatch.UpdatedAt;
                return Task.FromResult(true);
            }
        }

        private static bool IsDispatchDue(
            ProcessStepDispatchState? dispatch,
            DateTimeOffset now) =>
            dispatch is null ||
            dispatch.Status is ProcessStepDispatchStatus.Pending ||
            dispatch.Status is ProcessStepDispatchStatus.RetryableDispatchError &&
                (!dispatch.NextAttemptAt.HasValue || dispatch.NextAttemptAt.Value <= now) ||
            dispatch.Status is ProcessStepDispatchStatus.Dispatching &&
                dispatch.LockedUntil.HasValue &&
                dispatch.LockedUntil.Value <= now;

        private bool TryGetLeasedDispatch(
            ProcessStepRecord state,
            string leaseId,
            out ProcessStepDispatchState dispatch)
        {
            dispatch = null!;
            if (!States.TryGetValue(Guid.Parse(state.Id), out var current) ||
                current.Dispatch is not { } currentDispatch ||
                !string.Equals(currentDispatch.LeaseId, leaseId, StringComparison.Ordinal))
            {
                return false;
            }

            dispatch = currentDispatch;
            return true;
        }
    }

    private sealed class ProcessStepQueueWriterSpy : IProcessStepQueueWriter
    {
        public List<ProcessStepInitialisationMessage> Messages { get; } = [];

        public int ThrowOnEnqueueAttempts { get; set; }

        public Task EnqueueAsync(
            ProcessStepInitialisationMessage message,
            CancellationToken cancellationToken)
        {
            if (ThrowOnEnqueueAttempts > 0)
            {
                ThrowOnEnqueueAttempts--;
                throw new InvalidOperationException("Queue enqueue failed.");
            }

            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
