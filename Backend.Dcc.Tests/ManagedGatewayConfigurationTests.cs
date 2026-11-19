using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Backend.Database.Relational;
using Backend.Engine;
using Backend.Engine.Capabilities;
using Backend.Plugins.Configuration.Dcc;
using Backend.Plugins.Harness;
using Backend.Services.EngineManagement;
using Backend.Utilities;
using Dcc.GatewaySubmit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Backend.Dcc.Tests;

public class ManagedGatewayConfigurationTests
{
    [Test]
    public async Task Rotated_and_removed_overrides_reach_the_network_probe_and_GatewaySubmit_options(CancellationToken ct)
    {
        const string endpoint = "Dcc:Gateway:EndpointUrl";
        const string apiKey = "Dcc:Gateway:ApiKey";
        var observedPaths = new ConcurrentQueue<string>();
        var web = WebApplication.CreateSlimBuilder();
        web.Logging.ClearProviders();
        web.WebHost.UseUrls("http://127.0.0.1:0");
        await using var gateway = web.Build();
        gateway.MapMethods("/{source}", ["HEAD"], (HttpContext context) =>
        {
            observedPaths.Enqueue(context.Request.Path.Value!);
            return Results.Ok();
        });
        await gateway.StartAsync(ct);
        var deploymentUrl = gateway.Urls.Single() + "/deployment";
        var managedUrl = gateway.Urls.Single() + "/managed";
        var fileRoot = Directory.CreateTempSubdirectory("wf-managed-gateway-");
        var localReferences = new List<string>();
        var encryptedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WorkflowBuilder", "EngineManagement");
        try
        {
            var development = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                EnvironmentName = Environments.Development, DisableDefaults = true
            });
            development.Configuration[endpoint] = deploymentUrl;
            development.Configuration[apiKey] = "deployment-credential";
            development.Configuration["PluginRuntime:ConfigurationFileRoot"] = fileRoot.FullName;
            development.Services.AddEngineManagement();
            using var host = development.Build();
            var catalog = host.Services.GetRequiredService<ManagementSettingsCatalog>();
            var secrets = host.Services.GetRequiredService<IManagementSecretStore>();
            catalog.Find(apiKey)!.Secret.ShouldBeTrue();
            catalog.Find(endpoint)!.Accepts(managedUrl).ShouldBeTrue();

            await using var sql = new SqliteConnection("Data Source=:memory:");
            await sql.OpenAsync(ct);
            await using var database = new BlueprintDbContext(new DbContextOptionsBuilder<BlueprintDbContext>().UseSqlite(sql).Options);
            await database.Database.EnsureCreatedAsync(ct);
            var policies = new EngineManagementStore(database);
            var revision = await policies.SetSettingAsync(0, "operator", endpoint, new(managedUrl, null), ct);
            var parser = new PluginManifestParsingUtility(NullLogger<PluginManifestParsingUtility>.Instance);
            var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
            var manifest = await parser.ReadAsync(Path.Combine(fixtures, "Gateway"), ct);
            var unrelated = await parser.ReadAsync(Path.Combine(fixtures, "Town"), ct);
            using var files = new PluginRuntimeConfigurationService(development.Configuration, parser,
                NullLogger<PluginRuntimeConfigurationService>.Instance);
            var command = new ProcessCommandCapabilityProbe();
            var network = new HttpEndpointCapabilityProbe();
            var capabilities = new PluginRuntimeCapabilityService(parser,
                new CapabilityValidationService(development.Configuration, command, network),
                NullLogger<PluginRuntimeCapabilityService>.Instance, command, network);

            var initial = await RotateAsync();
            var firstLaunch = await AssertGatewayAsync(managedUrl, initial);
            var rotated = await RotateAsync();
            var policy = await policies.ReadAsync(ct);
            firstLaunch.NeedsReplacement(policy).ShouldBeTrue();
            (await secrets.ReadAsync(localReferences[0], ct) == initial).ShouldBeTrue();
            await AssertGatewayAsync(managedUrl, rotated);

            var isolated = await PluginLaunchConfiguration.ResolveAsync(unrelated, development.Configuration, policy,
                () => throw new InvalidOperationException("The unrelated Plugin requested a secret"), ct, catalog);
            isolated.Values[apiKey].ShouldBeNull();
            isolated.Values[endpoint].ShouldBeNull();
            isolated.IsPrivateEngineVariable("Dcc__Gateway__ApiKey").ShouldBeTrue();

            revision = await policies.SetSettingAsync(revision, "operator", apiKey, null, ct);
            await policies.SetSettingAsync(revision, "operator", endpoint, null, ct);
            await AssertGatewayAsync(deploymentUrl, "deployment-credential");
            observedPaths.ToArray().ShouldBe(new[] { "/managed", "/managed", "/deployment" });

            async Task<string> RotateAsync()
            {
                var value = Guid.NewGuid().ToString("N");
                var reference = await secrets.WriteAsync(apiKey, value, ct);
                Guid.TryParseExact(reference, "N", out _).ShouldBeTrue();
                localReferences.Add(reference);
                var ciphertext = await File.ReadAllBytesAsync(Path.Combine(encryptedRoot, reference), ct);
                (ciphertext.AsSpan().IndexOf(Encoding.UTF8.GetBytes(value)) < 0).ShouldBeTrue();
                revision = await policies.SetSettingAsync(revision, "operator", apiKey, new(null, reference), ct);
                var document = (await database.EngineFleetPolicies.AsNoTracking().SingleAsync(ct)).Document;
                document.Contains(value, StringComparison.Ordinal).ShouldBeFalse();
                return value;
            }

            async Task<PluginLaunchConfiguration> AssertGatewayAsync(string expectedUrl, string expectedKey)
            {
                var snapshot = await policies.ReadAsync(ct);
                var launch = await PluginLaunchConfiguration.ResolveAsync(manifest, development.Configuration, snapshot,
                    () => secrets, ct, catalog);
                (await capabilities.ValidateAsync(launch, "DCC gateway", ct)).Succeeded.ShouldBeTrue();
                var file = (await files.WriteFilesAsync(launch, "DCC gateway", ct)).Single();
                var plugin = Plugin.CreateBuilder<GatewaySubmitPlugin>("DCC gateway").RequireCapability<DccGatewayCapability>();
                plugin.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddJsonFile(file.FullName).Build());
                using var scope = plugin.Services.BuildServiceProvider();
                var options = scope.GetRequiredService<IOptions<GatewayOptions>>().Value;
                options.EndpointUrl.ShouldBe(new Uri(expectedUrl));
                (options.ApiKey?.Reveal() == expectedKey).ShouldBeTrue();
                return launch;
            }
        }
        finally
        {
            foreach (var reference in localReferences)
                File.Delete(Path.Combine(encryptedRoot, reference));
            fileRoot.Delete(recursive: true);
        }
    }
}
