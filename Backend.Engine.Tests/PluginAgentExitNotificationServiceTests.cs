using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backend.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Backend.Engine.Tests;

public sealed class PluginAgentExitNotificationServiceTests
{
    [Test]
    public async Task Non_shutdown_exit_with_inflight_jobs_notifies_failure_handler()
    {
        var jobId = Guid.NewGuid();
        var failureHandler = new Mock<IPluginAgentFailureHandler>();
        var service = CreateService(failureHandler, out _);

        await service.NotifyProcessExitAsync(
            "TestPlugin",
            exitCode: 1,
            shutdownRequested: false,
            new PluginRuntimeProcessExitSummary(ExitedBeforeReady: false, [jobId]),
            CancellationToken.None);

        failureHandler.Verify(
            handler => handler.HandlePluginAgentExitedAsync(
                It.Is<PluginAgentExitNotification>(notification =>
                    notification.PluginName == "TestPlugin" &&
                    notification.ExitCode == 1 &&
                    !notification.ShutdownRequested &&
                    notification.InFlightProcessStepIds.Contains(jobId)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task Shutdown_exit_does_not_notify_failure_handler()
    {
        var failureHandler = new Mock<IPluginAgentFailureHandler>();
        var service = CreateService(failureHandler, out _);

        await service.NotifyProcessExitAsync(
            "TestPlugin",
            exitCode: 0,
            shutdownRequested: true,
            new PluginRuntimeProcessExitSummary(ExitedBeforeReady: false, [Guid.NewGuid()]),
            CancellationToken.None);

        failureHandler.Verify(
            handler => handler.HandlePluginAgentExitedAsync(
                It.IsAny<PluginAgentExitNotification>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task Agent_exit_before_ready_records_telemetry()
    {
        var service = CreateService(new Mock<IPluginAgentFailureHandler>(), out var telemetry);

        await service.NotifyProcessExitAsync(
            "TestPlugin",
            exitCode: 1,
            shutdownRequested: false,
            new PluginRuntimeProcessExitSummary(ExitedBeforeReady: true, []),
            CancellationToken.None);

        telemetry.RecordedAgentExitedBeforeReady.ShouldBe(1);
    }

    private static PluginAgentExitNotificationService CreateService(
        Mock<IPluginAgentFailureHandler> failureHandler,
        out PluginRuntimeAgentTelemetry telemetry)
    {
        telemetry = new PluginRuntimeAgentTelemetry();
        return new PluginAgentExitNotificationService(
            failureHandler.Object,
            telemetry,
            NullLogger<PluginAgentExitNotificationService>.Instance);
    }
}
