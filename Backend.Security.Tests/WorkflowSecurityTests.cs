using System.Security.Claims;
using Backend.Security.Actors;
using Backend.Security.Authentication;
using Backend.Security.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Backend.Security.Tests;

public sealed class WorkflowSecurityTests
{
    [Test]
    public void Options_validator_rejects_missing_keycloak_settings_when_enabled()
    {
        var validator = new WorkflowAuthenticationOptionsValidator();

        var result = validator.Validate(null, new WorkflowAuthenticationOptions { Enabled = true });

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(failure => failure.Contains("Authority", StringComparison.Ordinal));
    }

    [Test]
    public async Task Keycloak_roles_are_mapped_from_realm_and_resource_access()
    {
        var principal = PrincipalWithClaims(
            new("realm_access", """{"roles":["workflowbuilder.admin"]}"""),
            new("resource_access", """{"workflow-builder-api":{"roles":["workflowbuilder.runtime_engineer"]}}"""));
        var transformer = new KeycloakRoleClaimsTransformation(Options.Create(new WorkflowAuthenticationOptions
        {
            Enabled = true,
            Keycloak = new WorkflowKeycloakOptions
            {
                Authority = "https://localhost:8080/realms/workflow-builder",
                Audience = "workflow-builder-api",
                SpaClientId = "workflow-builder-spa"
            }
        }));

        var transformed = await transformer.TransformAsync(principal);

        transformed.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ShouldBe([
            WorkflowRoles.Admin,
            WorkflowRoles.RuntimeEngineer
        ], ignoreOrder: true);
    }

    [Test]
    public void Actor_resolver_returns_authenticated_human_from_claims()
    {
        var resolver = CreateResolver(authEnabled: true);
        var principal = PrincipalWithClaims(
            new("sub", "user-123"),
            new("preferred_username", "developer"),
            new("email", "developer@example.test"),
            new(ClaimTypes.Role, WorkflowRoles.BlueprintAuthor));

        var actor = resolver.Resolve(principal);

        actor.Type.ShouldBe(ActorType.Human);
        actor.Id.ShouldBe("user-123");
        actor.DisplayName.ShouldBe("developer");
        actor.Email.ShouldBe("developer@example.test");
        actor.IsInRole(WorkflowRoles.BlueprintAuthor).ShouldBeTrue();
    }

    [Test]
    public void Actor_resolver_returns_development_actor_when_auth_disabled()
    {
        var resolver = CreateResolver(authEnabled: false);

        var actor = resolver.Resolve(null);

        actor.Type.ShouldBe(ActorType.System);
        actor.IsAuthenticated.ShouldBeTrue();
        actor.IsInRole(WorkflowRoles.Admin).ShouldBeTrue();
        actor.IsInRole(WorkflowRoles.ProcessOperator).ShouldBeTrue();
    }

    [Test]
    public async Task Coarse_authorization_allows_authenticated_general_access()
    {
        var service = new CoarseWorkflowAuthorizationService();
        var actor = ActorWithRoles();

        var decision = await service.AuthorizeAsync(actor, WorkflowAuthorizationActions.ProcessRead);

        decision.Allowed.ShouldBeTrue();
    }

    [Test]
    public async Task Coarse_authorization_checks_role_bound_actions()
    {
        var service = new CoarseWorkflowAuthorizationService();
        var actor = ActorWithRoles(WorkflowRoles.ProcessOperator);

        var allowed = await service.AuthorizeAsync(actor, WorkflowAuthorizationActions.ProcessStart);
        var denied = await service.AuthorizeAsync(actor, WorkflowAuthorizationActions.BlueprintCreate);

        allowed.Allowed.ShouldBeTrue();
        denied.Allowed.ShouldBeFalse();
    }

    [Test]
    public void Development_actor_includes_internal_publisher_role()
    {
        var resolver = CreateResolver(authEnabled: false);

        var actor = resolver.Resolve(null);

        actor.IsInRole(WorkflowRoles.InternalPublisher).ShouldBeTrue();
    }

    private static ClaimsPrincipalWorkflowActorResolver CreateResolver(bool authEnabled) =>
        new(
            new HttpContextAccessor(),
            Options.Create(new WorkflowAuthenticationOptions { Enabled = authEnabled }));

    private static WorkflowActor ActorWithRoles(params string[] roles) =>
        new(
            ActorType.Human,
            "user-123",
            "developer",
            "developer@example.test",
            roles.ToHashSet(StringComparer.Ordinal));

    private static ClaimsPrincipal PrincipalWithClaims(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "Test", ClaimTypes.Name, ClaimTypes.Role));
}
