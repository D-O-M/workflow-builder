using Backend.API.Infrastructure;
using Backend.Topology;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Backend.API.Controllers;

[ApiController]
[Route(WorkflowBuilderTopology.ClientConfig.Route)]
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
            ?? throw new InvalidOperationException($"{WorkflowBuilderTopology.ClientConfig.SignalRBaseUrlKey} is not configured.");
        var grafanaBaseUrl = NormalizeBaseUrl(_options.GrafanaBaseUrl)
            ?? throw new InvalidOperationException($"{WorkflowBuilderTopology.ClientConfig.GrafanaBaseUrlKey} is not configured.");

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
