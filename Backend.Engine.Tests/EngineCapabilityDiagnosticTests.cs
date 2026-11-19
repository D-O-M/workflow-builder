using System.Threading;
using System.Threading.Tasks;
using Backend.Engine.Capabilities;
using Backend.Engine.Controllers;
using Backend.Services.EngineManagement;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Shouldly;

namespace Backend.Engine.Tests;

public sealed class EngineCapabilityDiagnosticTests
{
    [Test]
    public async Task Forged_commands_and_configuration_paths_never_reach_the_probe()
    {
        var validator = new Mock<ICapabilityValidationService>(MockBehavior.Strict);
        var controller = new EngineCapabilitiesController(validator.Object, new ManagementSettingsCatalog());
        var git = CapabilityDescriptorReader.Describe(typeof(GitCliCapability));
        var gateway = CapabilityDescriptorReader.Describe(typeof(DccGatewayCapability));
        foreach (var forged in new[]
                 {
                     git with { Command = "cmd" },
                     git with { Arguments = "credential fill" },
                     git with { Id = "unregistered:command" },
                     gateway with { EndpointConfigKey = "Unrelated:Credentials" },
                     gateway with { EngineConfigurationKeys = ["Other:Secret"] }
                 })
        {
            var result = await controller.Validate(new CapabilityValidationRequest { Capabilities = [forged] }, CancellationToken.None);
            result.Result.ShouldBeOfType<BadRequestObjectResult>();
        }
        validator.VerifyNoOtherCalls();
    }
}
