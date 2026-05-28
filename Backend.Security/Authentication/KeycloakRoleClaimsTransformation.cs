using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Backend.Security.Authentication;

public sealed class KeycloakRoleClaimsTransformation : IClaimsTransformation
{
    private readonly IOptions<WorkflowAuthenticationOptions> _options;

    public KeycloakRoleClaimsTransformation(IOptions<WorkflowAuthenticationOptions> options)
    {
        _options = options;
    }

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity)
        {
            return Task.FromResult(principal);
        }

        var existingRoles = principal
            .FindAll(ClaimTypes.Role)
            .Select(claim => claim.Value)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var role in ExtractRoles(principal).ToArray())
        {
            if (existingRoles.Add(role))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
            }
        }

        return Task.FromResult(principal);
    }

    private IEnumerable<string> ExtractRoles(ClaimsPrincipal principal)
    {
        foreach (var role in ExtractRealmRoles(principal))
        {
            yield return role;
        }

        var keycloak = _options.Value.Keycloak;
        foreach (var role in ExtractResourceRoles(principal, keycloak.Audience))
        {
            yield return role;
        }

        foreach (var role in ExtractResourceRoles(principal, keycloak.SpaClientId))
        {
            yield return role;
        }
    }

    private static IEnumerable<string> ExtractRealmRoles(ClaimsPrincipal principal)
    {
        foreach (var claim in principal.FindAll("realm_access"))
        {
            using var document = ParseClaimValue(claim.Value);
            if (document is null ||
                !document.RootElement.TryGetProperty("roles", out var rolesElement))
            {
                continue;
            }

            foreach (var role in EnumerateStringArray(rolesElement))
            {
                yield return role;
            }
        }
    }

    private static IEnumerable<string> ExtractResourceRoles(ClaimsPrincipal principal, string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            yield break;
        }

        foreach (var claim in principal.FindAll("resource_access"))
        {
            using var document = ParseClaimValue(claim.Value);
            if (document is null ||
                !document.RootElement.TryGetProperty(clientId, out var clientElement) ||
                !clientElement.TryGetProperty("roles", out var rolesElement))
            {
                continue;
            }

            foreach (var role in EnumerateStringArray(rolesElement))
            {
                yield return role;
            }
        }
    }

    private static JsonDocument? ParseClaimValue(string value)
    {
        try
        {
            return JsonDocument.Parse(value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateStringArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(item.GetString()))
            {
                yield return item.GetString()!;
            }
        }
    }
}
