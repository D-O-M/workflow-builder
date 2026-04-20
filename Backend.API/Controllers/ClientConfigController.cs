using Backend.API.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Backend.API.Controllers;

[ApiController]
[Route("api/client-config")]
public sealed class ClientConfigController : ControllerBase
{
    private readonly ClientRuntimeConfigOptions _options;

    public ClientConfigController(IOptions<ClientRuntimeConfigOptions> options)
    {
        _options = options.Value;
    }

    [HttpGet]
    public ActionResult<ClientRuntimeConfigViewModel> Get()
    {
        var apiBaseUrl = NormalizeBaseUrl(_options.ApiBaseUrl)
            ?? $"{Request.Scheme}://{Request.Host.Value}";
        var signalRBaseUrl = NormalizeBaseUrl(_options.SignalRBaseUrl)
            ?? throw new InvalidOperationException("ClientConfig:SignalRBaseUrl is not configured.");
        var grafanaBaseUrl = NormalizeBaseUrl(_options.GrafanaBaseUrl)
            ?? throw new InvalidOperationException("ClientConfig:GrafanaBaseUrl is not configured.");

        return Ok(new ClientRuntimeConfigViewModel(
            apiBaseUrl,
            signalRBaseUrl,
            grafanaBaseUrl));
    }

    private static string? NormalizeBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value!.TrimEnd('/');
    }

    public sealed record ClientRuntimeConfigViewModel(
        string ApiBaseUrl,
        string SignalRBaseUrl,
        string GrafanaBaseUrl);
}
