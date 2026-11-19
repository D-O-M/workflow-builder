using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Backend.Security.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Backend.API.Tests;

public sealed partial class AuthenticationEndpointTests
{
    [Test]
    public async Task Recovery_requires_the_ProcessOperator_policy_and_the_deployment_switch()
    {
        await EnvironmentLock.WaitAsync();
        try
        {
            using var environment = new EnvironmentVariableScope(BuildConfiguration(authEnabled: true));
            await using var source = new ApiFactory(authEnabled: true);
            await using var factory = source.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "RecoveryTest";
                    options.DefaultForbidScheme = "RecoveryTest";
                    options.DefaultChallengeScheme = "RecoveryTest";
                }).AddScheme<AuthenticationSchemeOptions, TestRoleHandler>("RecoveryTest", null)));
            using var client = factory.CreateClient();
            var config = factory.Services.GetRequiredService<IConfiguration>();
            config["ProcessRecovery:Enabled"] = "true";
            const string route = "/api/process-recoveries/preview";
            (await client.PostAsJsonAsync(route, new { })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            client.DefaultRequestHeaders.Add("Test-Role", WorkflowRoles.BlueprintAuthor);
            (await client.PostAsJsonAsync(route, new { })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            client.DefaultRequestHeaders.Remove("Test-Role");
            client.DefaultRequestHeaders.Add("Test-Role", WorkflowRoles.ProcessOperator);
            config["ProcessRecovery:Enabled"] = "false";
            (await client.PostAsJsonAsync(route, new { })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            EnvironmentLock.Release();
        }
    }

    private sealed class TestRoleHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("Test-Role", out var role)) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim("sub", "operator-test"), new Claim(ClaimTypes.Role, role.ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
