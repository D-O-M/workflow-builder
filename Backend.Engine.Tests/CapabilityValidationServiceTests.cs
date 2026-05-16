using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backend.Engine.Capabilities;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Backend.Engine.Tests;

public sealed class CapabilityValidationServiceTests
{
    [Test]
    public async Task ValidateAsync_Reports_missing_required_plugin_configuration()
    {
        var service = CreateService(new Dictionary<string, string?>());
        var report = await service.ValidateAsync(
            [
                new CapabilityRequirementDescriptor
                {
                    Id = "configuration:test",
                    Kind = CapabilityKindNames.Configuration,
                    DisplayName = "Test config",
                    ConfigurationKeys = ["Tests:Required:Name"]
                }
            ],
            [
                new ConfigurationRequirementDescriptor
                {
                    Key = "Tests:Required:Name",
                    Required = true
                }
            ],
            CancellationToken.None);

        report.Succeeded.ShouldBeFalse();
        report.Results.Single().Status.ShouldBe(CapabilityValidationStatus.Missing);
        report.Results.Single().Message.ShouldNotBeNull();
        report.Results.Single().Message!.ShouldContain("Tests:Required:Name");
    }

    [Test]
    public async Task ValidateAsync_Allows_missing_optional_plugin_configuration()
    {
        var service = CreateService(new Dictionary<string, string?>());
        var report = await service.ValidateAsync(
            [
                new CapabilityRequirementDescriptor
                {
                    Id = "configuration:test",
                    Kind = CapabilityKindNames.Configuration,
                    DisplayName = "Test config",
                    ConfigurationKeys = ["Tests:Optional:Name"]
                }
            ],
            [
                new ConfigurationRequirementDescriptor
                {
                    Key = "Tests:Optional:Name",
                    Required = false
                }
            ],
            CancellationToken.None);

        report.Succeeded.ShouldBeTrue();
    }

    [Test]
    public async Task ValidateAsync_Uses_command_probe_for_software_capabilities()
    {
        var commandProbe = new FakeCommandProbe(true);
        var service = CreateService(new Dictionary<string, string?>(), commandProbe);

        var report = await service.ValidateAsync(
            [
                new CapabilityRequirementDescriptor
                {
                    Id = "software:command:git",
                    Kind = CapabilityKindNames.Software,
                    DisplayName = "Git",
                    Command = "git",
                    Arguments = "--version"
                }
            ],
            [],
            CancellationToken.None);

        report.Succeeded.ShouldBeTrue();
        commandProbe.SeenCommand.ShouldBe("git");
        commandProbe.SeenArguments.ShouldBe("--version");
    }

    [Test]
    public async Task ValidateAsync_Does_not_fail_report_for_missing_optional_capability()
    {
        var service = CreateService(new Dictionary<string, string?>(), new FakeCommandProbe(false));

        var report = await service.ValidateAsync(
            [
                new CapabilityRequirementDescriptor
                {
                    Id = "software:command:optional-tool",
                    Kind = CapabilityKindNames.Software,
                    DisplayName = "Optional tool",
                    Required = false,
                    Command = "optional-tool",
                    Arguments = "--version"
                }
            ],
            [],
            CancellationToken.None);

        report.Succeeded.ShouldBeTrue();
        report.Results.Single().Status.ShouldBe(CapabilityValidationStatus.Missing);
        report.Results.Single().Required.ShouldBeFalse();
    }

    private static CapabilityValidationService CreateService(
        IReadOnlyDictionary<string, string?> values,
        ICommandCapabilityProbe? commandProbe = null,
        IEndpointCapabilityProbe? endpointProbe = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        return new CapabilityValidationService(
            configuration,
            commandProbe ?? new FakeCommandProbe(true),
            endpointProbe ?? new FakeEndpointProbe(true));
    }

    private sealed class FakeCommandProbe : ICommandCapabilityProbe
    {
        private readonly bool _succeeded;

        public FakeCommandProbe(bool succeeded)
        {
            _succeeded = succeeded;
        }

        public string? SeenCommand { get; private set; }
        public string? SeenArguments { get; private set; }

        public Task<CommandProbeResult> ProbeAsync(
            string command,
            string arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            SeenCommand = command;
            SeenArguments = arguments;
            return Task.FromResult(new CommandProbeResult(_succeeded, _succeeded ? 0 : 1, "", _succeeded ? null : "missing"));
        }
    }

    private sealed class FakeEndpointProbe : IEndpointCapabilityProbe
    {
        private readonly bool _succeeded;

        public FakeEndpointProbe(bool succeeded)
        {
            _succeeded = succeeded;
        }

        public Task<EndpointProbeResult> ProbeAsync(
            Uri endpoint,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromResult(new EndpointProbeResult(_succeeded, _succeeded ? null : "not reachable"));
    }
}
