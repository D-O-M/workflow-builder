using Backend.Engine.Capabilities;
using Backend.Services.EngineManagement;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Engine.Controllers;

[ApiController]
[Route("api/engine-capabilities")]
public sealed class EngineCapabilitiesController : ControllerBase
{
    private readonly ICapabilityValidationService _capabilityValidationService;
    private readonly ManagementSettingsCatalog _catalog;

    public EngineCapabilitiesController(ICapabilityValidationService capabilityValidationService, ManagementSettingsCatalog catalog)
    {
        _capabilityValidationService = capabilityValidationService;
        _catalog = catalog;
    }

    [HttpGet]
    public ActionResult<EngineCapabilityInventory> Get() =>
        Ok(_capabilityValidationService.GetInventory());

    [HttpPost("validate")]
    public async Task<ActionResult<CapabilityValidationReport>> Validate(
        CapabilityValidationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var capabilities = TrustedCapabilityChecks.Resolve(request.Capabilities);
            var settings = TrustedCapabilityChecks.ConfigurationFor(capabilities, _catalog);
            if (request.ConfigurationRequirements.Any(input => !settings.Any(field => field.Key == input.Key)))
                return BadRequest("The request contains an undeclared capability setting.");

            return Ok(await _capabilityValidationService.ValidateAsync(capabilities, settings, cancellationToken));
        }
        catch (InvalidDataException)
        {
            return BadRequest("Only installed Engine capability checks can be requested.");
        }
    }
}

public sealed record CapabilityValidationRequest
{
    public List<CapabilityRequirementDescriptor> Capabilities { get; init; } = [];
    public List<ConfigurationRequirementDescriptor> ConfigurationRequirements { get; init; } = [];
}
