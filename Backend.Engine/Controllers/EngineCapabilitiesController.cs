using Backend.Engine.Capabilities;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Engine.Controllers;

[ApiController]
[Route("api/engine-capabilities")]
public sealed class EngineCapabilitiesController : ControllerBase
{
    private readonly ICapabilityValidationService _capabilityValidationService;

    public EngineCapabilitiesController(ICapabilityValidationService capabilityValidationService)
    {
        _capabilityValidationService = capabilityValidationService;
    }

    [HttpGet]
    public ActionResult<EngineCapabilityInventory> Get() =>
        Ok(_capabilityValidationService.GetInventory());

    [HttpPost("validate")]
    public async Task<ActionResult<CapabilityValidationReport>> Validate(
        CapabilityValidationRequest request,
        CancellationToken cancellationToken)
    {
        var report = await _capabilityValidationService.ValidateAsync(
            request.Capabilities,
            request.ConfigurationRequirements,
            cancellationToken);

        return Ok(report);
    }
}

public sealed record CapabilityValidationRequest
{
    public List<CapabilityRequirementDescriptor> Capabilities { get; init; } = [];
    public List<ConfigurationRequirementDescriptor> ConfigurationRequirements { get; init; } = [];
}
