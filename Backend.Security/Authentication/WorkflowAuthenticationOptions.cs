namespace Backend.Security.Authentication;

public sealed class WorkflowAuthenticationOptions
{
    public const string SectionName = "Authentication";

    public bool Enabled { get; set; }

    public WorkflowKeycloakOptions Keycloak { get; set; } = new();
}

public sealed class WorkflowKeycloakOptions
{
    public bool Enabled { get; set; } = true;

    public string Authority { get; set; } = string.Empty;

    public string Realm { get; set; } = "workflow-builder";

    public string Audience { get; set; } = "workflow-builder-api";

    public string SpaClientId { get; set; } = "workflow-builder-spa";

    public string Scope { get; set; } = "openid profile email";

    public bool RequireHttpsMetadata { get; set; } = true;
}
