using Backend.API.Infrastructure;
using Backend.Security.Authentication;
using Backend.Topology;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Backend.API.Controllers;

[AllowAnonymous]
[ApiController]
[Route(WorkflowBuilderTopology.ClientConfig.Route)]
public sealed class ClientConfigController : ControllerBase
{
    private readonly ClientRuntimeConfigOptions _options;
    private readonly WorkflowAuthenticationOptions _authOptions;
    private readonly ConditionalBranchAuthoringOptions _branchOptions;
    private readonly bool _processRecoveryEnabled;

    public ClientConfigController(
        IOptions<ClientRuntimeConfigOptions> options,
        IOptions<WorkflowAuthenticationOptions> authOptions,
        IOptions<ConditionalBranchAuthoringOptions> branchOptions,
        ProcessRecoveryAvailability recovery)
    {
        _options = options.Value;
        _authOptions = authOptions.Value;
        _branchOptions = branchOptions.Value;
        _processRecoveryEnabled = recovery.Enabled;
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
        var authentication = ResolveAuthentication(_options.Authentication.Enabled ?? _authOptions.Enabled);

        return Ok(new ClientRuntimeConfigViewModel(
            apiBaseUrl,
            signalRBaseUrl,
            grafanaBaseUrl,
            authentication,
            new ClientRuntimeFeaturesViewModel(_branchOptions.Enabled, _processRecoveryEnabled),
            ResolveAuthentication(_authOptions.Enabled || _authOptions.ManagementEnabled)));
    }

    private ClientRuntimeAuthenticationViewModel ResolveAuthentication(bool enabled)
    {
        if (!enabled)
        {
            return new ClientRuntimeAuthenticationViewModel(
                Enabled: false,
                Authority: string.Empty,
                ClientId: string.Empty,
                Scope: string.Empty,
                Audience: string.Empty);
        }

        var clientOptions = _options.Authentication;
        var authOptions = _authOptions;
        return new ClientRuntimeAuthenticationViewModel(
            Enabled: true,
            Authority: NormalizeBaseUrl(clientOptions.Authority) ?? NormalizeBaseUrl(authOptions.Keycloak.Authority) ?? string.Empty,
            ClientId: UseConfiguredOrFallback(clientOptions.ClientId, authOptions.Keycloak.SpaClientId),
            Scope: UseConfiguredOrFallback(clientOptions.Scope, authOptions.Keycloak.Scope),
            Audience: UseConfiguredOrFallback(clientOptions.Audience, authOptions.Keycloak.Audience));
    }

    private static string UseConfiguredOrFallback(string? configured, string fallback) =>
        string.IsNullOrWhiteSpace(configured) ? fallback : configured;

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
        string GrafanaBaseUrl,
        ClientRuntimeAuthenticationViewModel Authentication,
        ClientRuntimeFeaturesViewModel Features,
        ClientRuntimeAuthenticationViewModel ManagementAuthentication);

    public sealed record ClientRuntimeFeaturesViewModel(bool ConditionalBranchAuthoring, bool ProcessRecoveryEnabled);

    public sealed record ClientRuntimeAuthenticationViewModel(
        bool Enabled,
        string Authority,
        string ClientId,
        string Scope,
        string Audience);
}
