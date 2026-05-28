using System.Security.Claims;
using System.Text.Encodings.Web;
using Backend.Security.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backend.Security.Authentication;

public sealed class DevelopmentWorkflowAuthenticationHandler
    : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public DevelopmentWorkflowAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "local-development"),
            new("sub", "local-development"),
            new("preferred_username", "developer"),
            new(ClaimTypes.Name, "Local Development")
        };

        claims.AddRange(WorkflowRoles.DevelopmentRoles.Select(role => new Claim(ClaimTypes.Role, role)));

        var identity = new ClaimsIdentity(
            claims,
            WorkflowAuthenticationSchemes.Development,
            ClaimTypes.Name,
            ClaimTypes.Role);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
