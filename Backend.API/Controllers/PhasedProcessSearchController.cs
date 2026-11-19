using Backend.API.Services;
using Backend.Models.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Backend.API.Controllers;

[ApiController]
[Route("api/phased-processes")]
public sealed class PhasedProcessSearchController(PhasedProcessSearchService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PhasedProcessSearchPageViewModel>> Search(
        [FromQuery] string? q, [FromQuery] string? status,
        [FromQuery] string? continuationToken, [FromQuery] int pageSize = 25)
    {
        try
        {
            return Ok(await service.SearchAsync(q, status, pageSize, continuationToken, HttpContext.RequestAborted));
        }
        catch (InvalidDataException exception)
        {
            return Problem(title: "Invalid Phase Series search", detail: exception.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }
}
