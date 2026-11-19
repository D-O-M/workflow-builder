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
}
