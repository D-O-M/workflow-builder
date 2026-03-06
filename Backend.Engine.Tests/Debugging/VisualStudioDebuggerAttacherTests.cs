using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Backend.Engine.Debugging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;

namespace Backend.Engine.Tests.Debugging;

public class VisualStudioDebuggerAttacherTests
{
    [Test]
    public async Task TryAttachAsync_SkipsWhenFeatureDisabled()
    {
        var options = Options.Create(new DebuggerAutoAttachOptions
        {
            Enabled = false,
            SolutionPaths = new List<string> { "C:/repo/Backend.sln" }
        });

        var env = CreateDevelopmentEnvironment();
        var provider = new Mock<IVisualStudioInstanceProvider>(MockBehavior.Strict);
        var guard = new Mock<IDebuggerAttachmentGuard>(MockBehavior.Strict);
        guard.Setup(g => g.IsDebuggerAttached(It.IsAny<Process>())).Returns(false);

        var attacher = new VisualStudioDebuggerAttacher(
            NullLogger<VisualStudioDebuggerAttacher>.Instance,
            env,
            options,
            provider.Object,
            guard.Object,
            new ImmediateDelay(),
            new OperatingSystemStub(true));

        await attacher.TryAttachAsync(Process.GetCurrentProcess(), CancellationToken.None);

        provider.Verify(p => p.GetRunningInstances(), Times.Never);
    }

    [Test]
    public async Task TryAttachAsync_SkipsWhenDebuggerAlreadyAttached()
    {
        var options = Options.Create(new DebuggerAutoAttachOptions
        {
            Enabled = true,
            SolutionPaths = new List<string> { "C:/repo/Backend.sln" }
        });

        var env = CreateDevelopmentEnvironment();
        var provider = new Mock<IVisualStudioInstanceProvider>(MockBehavior.Strict);
        var guard = new Mock<IDebuggerAttachmentGuard>(MockBehavior.Strict);
        guard.Setup(g => g.IsDebuggerAttached(It.IsAny<Process>())).Returns(true);

        var attacher = new VisualStudioDebuggerAttacher(
            NullLogger<VisualStudioDebuggerAttacher>.Instance,
            env,
            options,
            provider.Object,
            guard.Object,
            new ImmediateDelay(),
            new OperatingSystemStub(true));

        await attacher.TryAttachAsync(Process.GetCurrentProcess(), CancellationToken.None);

        provider.Verify(p => p.GetRunningInstances(), Times.Never);
    }

    [Test]
    public async Task TryAttachAsync_AttachesAfterPolling()
    {
        var options = Options.Create(new DebuggerAutoAttachOptions
        {
            Enabled = true,
            SolutionPaths = new List<string> { "C:/repo/Backend.sln" },
            TimeoutMilliseconds = 200,
            PollingIntervalMilliseconds = 10
        });

        var env = CreateDevelopmentEnvironment();
        var instance = new FakeInstance("C:/repo/Backend.sln", AttachResult.ProcessNotFound, AttachResult.Attached);
        var provider = new FakeInstanceProvider(
            new List<IVisualStudioInstance> { instance },
            new List<IVisualStudioInstance> { instance });
        var guard = new Mock<IDebuggerAttachmentGuard>(MockBehavior.Strict);
        guard.Setup(g => g.IsDebuggerAttached(It.IsAny<Process>())).Returns(false);

        var attacher = new VisualStudioDebuggerAttacher(
            NullLogger<VisualStudioDebuggerAttacher>.Instance,
            env,
            options,
            provider,
            guard.Object,
            new ImmediateDelay(),
            new OperatingSystemStub(true));

        await attacher.TryAttachAsync(Process.GetCurrentProcess(), CancellationToken.None);

        instance.AttachAttempts.ShouldBe(2);
    }

    [Test]
    public async Task TryAttachAsync_LaunchesVisualStudioWhenMissing()
    {
        var options = Options.Create(new DebuggerAutoAttachOptions
        {
            Enabled = true,
            SolutionPaths = new List<string> { "C:/repo/Backend.sln" },
            LaunchIfMissing = true,
            LaunchSolutionPaths = new List<string> { "C:/repo/Backend.sln" },
            TimeoutMilliseconds = 100,
            PollingIntervalMilliseconds = 10
        });

        var env = CreateDevelopmentEnvironment();
        var provider = new FakeInstanceProvider(Array.Empty<IVisualStudioInstance>());
        var guard = new Mock<IDebuggerAttachmentGuard>(MockBehavior.Strict);
        guard.Setup(g => g.IsDebuggerAttached(It.IsAny<Process>())).Returns(false);

        var attacher = new VisualStudioDebuggerAttacher(
            NullLogger<VisualStudioDebuggerAttacher>.Instance,
            env,
            options,
            provider,
            guard.Object,
            new ImmediateDelay(),
            new OperatingSystemStub(true));

        await attacher.TryAttachAsync(Process.GetCurrentProcess(), CancellationToken.None);

        provider.LaunchRequests.ShouldContain("C:/repo/Backend.sln");
    }

    private static IHostEnvironment CreateDevelopmentEnvironment()
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(Environments.Development);
        return env.Object;
    }

    private sealed class ImmediateDelay : IAsyncDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeInstanceProvider : IVisualStudioInstanceProvider
    {
        private readonly Queue<IReadOnlyList<IVisualStudioInstance>> _sequences;

        public List<string> LaunchRequests { get; } = new();

        public FakeInstanceProvider(params IReadOnlyList<IVisualStudioInstance>[] sequences)
        {
            _sequences = new Queue<IReadOnlyList<IVisualStudioInstance>>(sequences);
        }

        public IReadOnlyList<IVisualStudioInstance> GetRunningInstances()
        {
            return _sequences.Count > 0 ? _sequences.Dequeue() : Array.Empty<IVisualStudioInstance>();
        }

        public Task<bool> TryLaunchAsync(string solutionPath, CancellationToken cancellationToken)
        {
            LaunchRequests.Add(solutionPath);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeInstance : IVisualStudioInstance
    {
        private readonly Queue<AttachResult> _results;

        public FakeInstance(string? solutionPath, params AttachResult[] results)
        {
            SolutionPath = solutionPath;
            _results = new Queue<AttachResult>(results.Length == 0
                ? new[] { AttachResult.Attached }
                : results);
        }

        public string? SolutionPath { get; }

        public int AttachAttempts { get; private set; }

        public AttachResult TryAttachToProcess(int processId)
        {
            AttachAttempts++;
            return _results.Count > 0 ? _results.Dequeue() : AttachResult.Attached;
        }
    }

    private sealed class OperatingSystemStub : IOperatingSystemInformation
    {
        public OperatingSystemStub(bool isWindows, bool isLinux = false)
        {
            IsWindowsResult = isWindows;
            IsLinuxResult = isLinux;
        }

        public bool IsWindowsResult { get; }

        public bool IsLinuxResult { get; }

        public bool IsWindows() => IsWindowsResult;

        public bool IsLinux() => IsLinuxResult;
    }
}
