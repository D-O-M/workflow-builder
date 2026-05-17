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
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Backend.Engine.Tests;

public sealed class ProcessStepDispatchServiceTests
{
    [Test]
    public async Task Dispatch_failure_keeps_intent_retryable_and_worker_later_sends()
    {
        var repository = new InMemoryProcessStepRepository();
        var queueWriter = new ProcessStepQueueWriterSpy { ThrowOnEnqueueAttempts = 1 };
        var service = CreateService(repository, queueWriter);
        var step = CreateStep();
        repository.States[Guid.Parse(step.Id)] = step;

        var firstAttempt = await service.TryDispatchAsync(step, CancellationToken.None);

        firstAttempt.ShouldBeFalse();
        queueWriter.Messages.Count.ShouldBe(0);
        step.Dispatch.ShouldNotBeNull();
        step.Dispatch.Status.ShouldBe(ProcessStepDispatchStatus.RetryableDispatchError);
        repository.MarkSucceededCount.ShouldBe(0);

        queueWriter.ThrowOnEnqueueAttempts = 0;
        step.Dispatch.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        var dispatchedCount = await service.DrainDueDispatchesAsync(10, CancellationToken.None);

        dispatchedCount.ShouldBe(1);
        queueWriter.Messages.Count.ShouldBe(1);
        queueWriter.Messages[0].ProcessStepId.ShouldBe(Guid.Parse(step.Id));
        step.Dispatch.Status.ShouldBe(ProcessStepDispatchStatus.Dispatched);
        repository.MarkSucceededCount.ShouldBe(1);
    }

    [Test]
    public async Task Duplicate_worker_execution_claims_dispatch_once()
    {
        var repository = new InMemoryProcessStepRepository();
        var queueWriter = new ProcessStepQueueWriterSpy();
        var service = CreateService(repository, queueWriter);
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
    }

    [Test]
    public async Task Terminal_steps_are_not_dispatched()
    {
        var repository = new InMemoryProcessStepRepository();
        var queueWriter = new ProcessStepQueueWriterSpy();
        var service = CreateService(repository, queueWriter);
        var step = CreateStep(ProcessStepStatus.Completed);
        repository.States[Guid.Parse(step.Id)] = step;

        var dispatchedCount = await service.DrainDueDispatchesAsync(10, CancellationToken.None);
        var directAttempt = await service.TryDispatchAsync(step, CancellationToken.None);

        dispatchedCount.ShouldBe(0);
        directAttempt.ShouldBeFalse();
        queueWriter.Messages.Count.ShouldBe(0);
        step.Dispatch.ShouldNotBeNull();
        step.Dispatch.Status.ShouldBe(ProcessStepDispatchStatus.Pending);
    }

    [Test]
    public async Task Dispatch_is_marked_completed_only_after_queue_send_succeeds()
    {
        var repository = new InMemoryProcessStepRepository();
        var queueWriter = new ProcessStepQueueWriterSpy { ThrowOnEnqueueAttempts = 1 };
        var service = CreateService(repository, queueWriter);
        var step = CreateStep();
        repository.States[Guid.Parse(step.Id)] = step;

        await service.TryDispatchAsync(step, CancellationToken.None);

        step.Dispatch.ShouldNotBeNull();
        step.Dispatch.Status.ShouldNotBe(ProcessStepDispatchStatus.Dispatched);
        step.Dispatch.DispatchedAt.ShouldBeNull();
        repository.MarkSucceededCount.ShouldBe(0);
    }

    private static ProcessStepDispatchService CreateService(
        InMemoryProcessStepRepository repository,
        ProcessStepQueueWriterSpy queueWriter) =>
        new(
            repository,
            queueWriter,
            new ProcessStepDispatchOptions
            {
                RetryBaseDelay = TimeSpan.FromMilliseconds(1),
                RetryMaxDelay = TimeSpan.FromMilliseconds(10),
                LeaseDuration = TimeSpan.FromSeconds(30)
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

        public int MarkSucceededCount { get; private set; }

        public Task<IReadOnlyList<ProcessStepRecord>> GetDueStepDispatchesAsync(
            DateTimeOffset now,
            int maxItemCount,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
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

                MarkSucceededCount++;
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
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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
