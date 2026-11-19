using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Infrastructure;
using Backend.Database.Relational;
using Backend.Database.Relational.Entities;
using Backend.Interfaces.Repositories;
using Backend.Security.Authorization;
using Backend.Security.Authentication;
using Backend.Services.EngineManagement;
using Backend.Services.PluginRuntimeStatus;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Shouldly;

namespace Backend.API.Tests;

public sealed partial class AuthenticationEndpointTests
{
    [Test]
    public async Task Management_only_sign_in_checks_bearer_roles_and_audits_the_signed_subject()
    {
        await EnvironmentLock.WaitAsync();
        try
        {
            var config = BuildConfiguration(authEnabled: false);
            config["EngineManagement:Enabled"] = "true";
            config["Authentication:ManagementEnabled"] = "true";
            using var _ = new EnvironmentVariableScope(config);
            var issuer = config["Authentication:Keycloak:Authority"]!;
            var audience = config["Authentication:Keycloak:Audience"]!;
            var signingKey = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
            var connectionString = $"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            await using var keeper = new SqliteConnection(connectionString);
            await keeper.OpenAsync();
            await using var source = new ApiFactory(authEnabled: false);
            await using var factory = source.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IBlueprintDbContextOptionsConfigurator>(
                    new SqliteOptions(connectionString)));
                services.Replace(ServiceDescriptor.Singleton<IPluginTaskVersionRuntimeStatusStore,
                    InMemoryPluginTaskVersionRuntimeStatusStore>());
                services.PostConfigure<JwtBearerOptions>(WorkflowAuthenticationSchemes.ManagementBearer, options =>
                {
                    var discovery = new OpenIdConnectConfiguration { Issuer = issuer };
                    discovery.SigningKeys.Add(signingKey);
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(discovery);
                });
            }));
            using var client = factory.CreateClient();
            const string settings = "/api/engine-management/settings";
            (await client.GetAsync("/WeatherForecast")).EnsureSuccessStatusCode();
            var bootstrap = await client.GetFromJsonAsync<JsonElement>("/api/client-config");
            bootstrap.GetProperty("authentication").GetProperty("enabled").GetBoolean().ShouldBeFalse();
            bootstrap.GetProperty("managementAuthentication").GetProperty("enabled").GetBoolean().ShouldBeTrue();
            (await client.GetAsync(settings)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(WorkflowRoles.Admin, "wrong-api"));
            (await client.GetAsync(settings)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(WorkflowRoles.BlueprintAuthor, audience));
            (await client.GetAsync(settings)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(WorkflowRoles.RuntimeEngineer, audience));
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BlueprintDbContext>();
            await db.Database.EnsureCreatedAsync();
            (await client.PutAsJsonAsync(settings + "/Dcc:Gateway:EndpointUrl",
                new { expectedRevision = 0, value = "https://example.test" })).EnsureSuccessStatusCode();
            (await db.EngineManagementChanges.SingleAsync()).Actor.ShouldBe("signed-operator");

            string Token(string role, string target) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = issuer,
                Audience = target,
                Claims = new Dictionary<string, object>
                {
                    ["sub"] = "signed-operator",
                    ["realm_access"] = JsonSerializer.SerializeToElement(new { roles = new[] { role } })
                },
                SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256),
                Expires = DateTime.UtcNow.AddMinutes(5)
            });
        }
        finally { EnvironmentLock.Release(); }
    }

    [Test]
    public async Task Management_rejects_the_synthetic_admin_when_authentication_is_disabled()
    {
        await EnvironmentLock.WaitAsync();
        try
        {
            var config = BuildConfiguration(authEnabled: false);
            config["EngineManagement:Enabled"] = "true";
            using var _ = new EnvironmentVariableScope(config);
            await using var factory = new ApiFactory(authEnabled: false);
            using var client = factory.CreateClient();
            (await client.GetAsync("/api/engine-management/settings")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        finally { EnvironmentLock.Release(); }
    }

    [Test]
    public async Task Management_authorizes_edits_and_reports_applied_policy_without_exposing_credentials()
    {
        await EnvironmentLock.WaitAsync();
        try
        {
            var config = BuildConfiguration(authEnabled: true);
            config["EngineManagement:Enabled"] = "true";
            using var _ = new EnvironmentVariableScope(config);
            var connectionString = $"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            await using var keeper = new SqliteConnection(connectionString);
            await keeper.OpenAsync();
            var secrets = new TestSecretStore();
            await using var source = new ApiFactory(authEnabled: true);
            await using var factory = source.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IBlueprintDbContextOptionsConfigurator>(
                    new SqliteOptions(connectionString)));
                services.Replace(ServiceDescriptor.Singleton<IManagementSecretStore>(secrets));
                services.Replace(ServiceDescriptor.Singleton<IPluginTaskVersionRuntimeStatusStore,
                    InMemoryPluginTaskVersionRuntimeStatusStore>());
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "ManagementTest";
                    options.DefaultChallengeScheme = "ManagementTest";
                    options.DefaultForbidScheme = "ManagementTest";
                }).AddScheme<AuthenticationSchemeOptions, TestRoleHandler>("ManagementTest", null);
            }));
            using var client = factory.CreateClient();
            int pluginId, versionId;
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<BlueprintDbContext>();
                await db.Database.EnsureCreatedAsync();
                var plugin = new TaskDefinition
                {
                    Name = "Dcc.GatewaySubmit",
                    Versions = [new TaskVersion { Path = "gateway", Version = new Version(1, 0, 1) }]
                };
                db.TaskDefinitions.Add(plugin);
                await db.SaveChangesAsync();
                pluginId = plugin.Id;
                versionId = plugin.Versions[0].Id;
            }

            const string root = "/api/engine-management";
            const string secretUrl = root + "/settings/Dcc:Gateway:ApiKey";
            (await client.GetAsync(root + "/settings")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            client.DefaultRequestHeaders.Add("Test-Role", WorkflowRoles.BlueprintAuthor);
            (await client.GetAsync(root + "/settings")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await client.PutAsJsonAsync(secretUrl, new { expectedRevision = 0, value = "denied" })).StatusCode
                .ShouldBe(HttpStatusCode.Forbidden);
            client.DefaultRequestHeaders.Remove("Test-Role");
            client.DefaultRequestHeaders.Add("Test-Role", WorkflowRoles.RuntimeEngineer);
            var deployment = factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
            deployment["EngineManagement:WritesEnabled"] = "false";
            (await client.GetAsync(root + "/settings")).EnsureSuccessStatusCode();
            (await client.PutAsJsonAsync(secretUrl, new { expectedRevision = 0, value = "blocked" })).StatusCode
                .ShouldBe(HttpStatusCode.ServiceUnavailable);
            deployment["EngineManagement:WritesEnabled"] = "true";
            (await client.PutAsJsonAsync(root + "/settings/PATH", new { expectedRevision = 0, value = "x" }))
                .StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await client.PutAsJsonAsync(root + "/settings/Dcc:Gateway:EndpointUrl", new { expectedRevision = 0, value = "bad" }))
                .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await client.PutAsJsonAsync(secretUrl, new { expectedRevision = 0, value = "first-credential" })).EnsureSuccessStatusCode();
            var listing = await client.GetStringAsync(root + "/settings");
            listing.ShouldContain("Dcc:Gateway:ApiKey");
            listing.ShouldNotContain("first-credential");
            (await client.PutAsJsonAsync(secretUrl, new { expectedRevision = 0, value = "stale" })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
            secrets.Fail = true;
            (await client.PutAsJsonAsync(secretUrl, new { expectedRevision = 1, value = "bad-write" })).StatusCode
                .ShouldBe(HttpStatusCode.ServiceUnavailable);
            secrets.Fail = false;
            (await client.PutAsJsonAsync(secretUrl, new { expectedRevision = 1, value = "rotated-credential" })).EnsureSuccessStatusCode();
            (await client.PutAsJsonAsync($"{root}/plugins/{pluginId}/versions/{versionId}", new { expectedRevision = 2, enabled = false })).EnsureSuccessStatusCode();
            (await client.PutAsJsonAsync($"{root}/plugins/{pluginId}", new { expectedRevision = 3, enabled = false })).EnsureSuccessStatusCode();
            (await client.PutAsJsonAsync($"{root}/plugins/{pluginId}", new { expectedRevision = 4, enabled = true })).EnsureSuccessStatusCode();
            (await client.GetStringAsync(root + "/plugins")).ShouldContain("DisabledByOperator");
            (await client.GetStringAsync("/Tasks")).ShouldContain("DisabledByOperator");
            (await client.GetStringAsync(root + "/fleet")).ShouldContain("Awaiting an Engine");

            using (var scope = factory.Services.CreateScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<EngineManagementStore>();
                var policy = await store.ReadAsync(default);
                policy.DisabledPlugins.Count.ShouldBe(0);
                policy.DisabledVersions.ShouldContain(versionId);
                var setting = policy.Settings["Dcc:Gateway:ApiKey"];
                setting.Value.ShouldBeNull();
                (await secrets.ReadAsync(setting.SecretReference!, default)).ShouldBe("rotated-credential");
                var db = scope.ServiceProvider.GetRequiredService<BlueprintDbContext>();
                (await db.EngineFleetPolicies.SingleAsync()).Document.ShouldNotContain("credential");
                var audit = await db.EngineManagementChanges.AsNoTracking().ToListAsync();
                audit.Count.ShouldBe(5);
                audit.All(entry => entry.Actor == "operator-test").ShouldBeTrue();
                JsonSerializer.Serialize(audit).ShouldNotContain("credential");
                await store.ReportAsync("test-engine", 4, null, default);
            }
            (await client.GetStringAsync(root + "/fleet")).ShouldContain("Applying");
            using (var scope = factory.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<EngineManagementStore>().ReportAsync("test-engine", 5, null, default);
            (await client.GetStringAsync(root + "/fleet")).ShouldContain("Applied");
            client.DefaultRequestHeaders.Remove("Test-Role");
            client.DefaultRequestHeaders.Add("Test-Role", WorkflowRoles.Admin);
            (await client.DeleteAsync(secretUrl + "?expectedRevision=5")).EnsureSuccessStatusCode();
            (await client.GetStringAsync(root + "/settings")).ShouldNotContain("\"overridden\":true");
        }
        finally { EnvironmentLock.Release(); }
    }

    private sealed class SqliteOptions(string connectionString) : IBlueprintDbContextOptionsConfigurator
    {
        public void Configure(DbContextOptionsBuilder optionsBuilder, IServiceProvider serviceProvider) =>
            optionsBuilder.UseSqlite(connectionString);
    }

    private sealed class TestSecretStore : IManagementSecretStore
    {
        public bool Fail { get; set; }
        private readonly Dictionary<string, string> _versions = [];
        public Task<string> WriteAsync(string key, string value, CancellationToken ct)
        {
            if (Fail) throw new InvalidOperationException("store failed");
            var reference = Guid.NewGuid().ToString("N");
            _versions.Add(reference, value);
            return Task.FromResult(reference);
        }
        public Task<string> ReadAsync(string reference, CancellationToken ct) => Task.FromResult(_versions[reference]);
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
