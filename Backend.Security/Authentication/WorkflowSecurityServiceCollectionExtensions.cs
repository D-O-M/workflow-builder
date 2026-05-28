using System.Security.Claims;
using Backend.Security.Actors;
using Backend.Security.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Backend.Security.Authentication;

public static class WorkflowSecurityServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowSecurity(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        Action<JwtBearerOptions>? configureJwtBearer = null)
    {
        var authSection = configuration.GetSection(WorkflowAuthenticationOptions.SectionName);
        var authOptions = authSection.Get<WorkflowAuthenticationOptions>() ?? new WorkflowAuthenticationOptions();

        services.AddHttpContextAccessor();
        services.AddOptions<WorkflowAuthenticationOptions>()
            .Bind(authSection)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<WorkflowAuthenticationOptions>, WorkflowAuthenticationOptionsValidator>();
        services.AddScoped<IWorkflowActorResolver, ClaimsPrincipalWorkflowActorResolver>();
        services.AddSingleton<IWorkflowAuthorizationService, CoarseWorkflowAuthorizationService>();

        if (authOptions.Enabled)
        {
            services
                .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    ConfigureJwtBearer(options, authOptions, environment);
                    configureJwtBearer?.Invoke(options);
                });
            services.AddTransient<IClaimsTransformation, KeycloakRoleClaimsTransformation>();
        }
        else
        {
            services
                .AddAuthentication(WorkflowAuthenticationSchemes.Development)
                .AddScheme<AuthenticationSchemeOptions, DevelopmentWorkflowAuthenticationHandler>(
                    WorkflowAuthenticationSchemes.Development,
                    _ => { });
        }

        services.AddAuthorization(AddWorkflowAuthorizationPolicies);
        return services;
    }

    private static void ConfigureJwtBearer(
        JwtBearerOptions options,
        WorkflowAuthenticationOptions authOptions,
        IHostEnvironment environment)
    {
        var keycloak = authOptions.Keycloak;
        var authority = keycloak.Authority.TrimEnd('/');
        options.Authority = authority;
        options.Audience = keycloak.Audience;
        options.RequireHttpsMetadata = keycloak.RequireHttpsMetadata && !environment.IsDevelopment();
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = authority,
            ValidateAudience = true,
            ValidAudience = keycloak.Audience,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            NameClaimType = "preferred_username",
            RoleClaimType = ClaimTypes.Role
        };
    }

    private static void AddWorkflowAuthorizationPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(
            WorkflowAuthorizationPolicies.Authenticated,
            policy => policy.RequireAuthenticatedUser());
        options.AddPolicy(
            WorkflowAuthorizationPolicies.BlueprintAuthor,
            policy => policy.RequireRole(WorkflowRoles.BlueprintAuthor, WorkflowRoles.Admin));
        options.AddPolicy(
            WorkflowAuthorizationPolicies.ProcessOperator,
            policy => policy.RequireRole(WorkflowRoles.ProcessOperator, WorkflowRoles.Admin));
        options.AddPolicy(
            WorkflowAuthorizationPolicies.RuntimeEngineer,
            policy => policy.RequireRole(WorkflowRoles.RuntimeEngineer, WorkflowRoles.Admin));
        options.AddPolicy(
            WorkflowAuthorizationPolicies.Auditor,
            policy => policy.RequireRole(WorkflowRoles.Auditor, WorkflowRoles.Admin));
        options.AddPolicy(
            WorkflowAuthorizationPolicies.Admin,
            policy => policy.RequireRole(WorkflowRoles.Admin));
    }
}
