using Microsoft.Extensions.Options;

namespace Backend.Security.Authentication;

public sealed class WorkflowAuthenticationOptionsValidator
    : IValidateOptions<WorkflowAuthenticationOptions>
{
    public ValidateOptionsResult Validate(string? name, WorkflowAuthenticationOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var errors = new List<string>();
        if (!options.Keycloak.Enabled)
        {
            errors.Add("Authentication:Keycloak:Enabled must be true when Authentication:Enabled is true.");
        }

        if (string.IsNullOrWhiteSpace(options.Keycloak.Authority))
        {
            errors.Add("Authentication:Keycloak:Authority is required when authentication is enabled.");
        }
        else if (!Uri.TryCreate(options.Keycloak.Authority, UriKind.Absolute, out _))
        {
            errors.Add("Authentication:Keycloak:Authority must be an absolute URI.");
        }

        if (string.IsNullOrWhiteSpace(options.Keycloak.Audience))
        {
            errors.Add("Authentication:Keycloak:Audience is required when authentication is enabled.");
        }

        if (string.IsNullOrWhiteSpace(options.Keycloak.SpaClientId))
        {
            errors.Add("Authentication:Keycloak:SpaClientId is required when authentication is enabled.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
