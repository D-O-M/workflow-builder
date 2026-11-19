using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.API;
using Backend.Topology;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Backend.API.Tests;

public sealed partial class AuthenticationEndpointTests
{
    private static readonly SemaphoreSlim EnvironmentLock = new(1, 1);

    [Test]
    public async Task Protected_controller_returns_401_without_token_when_auth_enabled()
    {
        await EnvironmentLock.WaitAsync();
        try
        {
            using var _ = new EnvironmentVariableScope(BuildConfiguration(authEnabled: true));
            await using var factory = new ApiFactory(authEnabled: true);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });

            var response = await client.GetAsync("/WeatherForecast");

            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        finally
        {
            EnvironmentLock.Release();
        }
    }

    [Test]
    public async Task Client_config_is_anonymous_and_exposes_auth_settings()
    {
        await EnvironmentLock.WaitAsync();
        try
        {
            using var _ = new EnvironmentVariableScope(BuildConfiguration(authEnabled: true));
            await using var factory = new ApiFactory(authEnabled: true);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });

            var response = await client.GetAsync($"/{WorkflowBuilderTopology.ClientConfig.Route}");

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var config = await response.Content.ReadFromJsonAsync<ClientConfigResponse>();
            config.ShouldNotBeNull();
            config.Authentication.Enabled.ShouldBeTrue();
            config.Authentication.Authority.ShouldBe("https://localhost:8080/realms/workflow-builder");
            config.Authentication.ClientId.ShouldBe("workflow-builder-spa");
            config.Features.ConditionalBranchAuthoring.ShouldBeTrue();
        }
        finally
        {
            EnvironmentLock.Release();
        }
    }

    private sealed class ApiFactory : WebApplicationFactory<Program>
    {
        private readonly bool _authEnabled;

        public ApiFactory(bool authEnabled)
        {
            _authEnabled = authEnabled;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration(configuration =>
            {
                configuration.AddInMemoryCollection(BuildConfiguration(_authEnabled));
            });
        }
    }

    private static Dictionary<string, string?> BuildConfiguration(bool authEnabled) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [$"ConnectionStrings:{WorkflowBuilderTopology.Sql.TasksDbConnectionName}"] = "Server=(local);",
            [WorkflowBuilderTopology.Cosmos.ConnectionNameKey] = WorkflowBuilderTopology.Cosmos.DefaultDatabaseName,
            [$"ConnectionStrings:{WorkflowBuilderTopology.Cosmos.DefaultDatabaseName}"] = "AccountEndpoint=https://test/;",
            [WorkflowBuilderTopology.Cosmos.DatabaseNameKey] = WorkflowBuilderTopology.Cosmos.DefaultDatabaseName,
            [WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.ConnectionNameKey] = WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.ConnectionName,
            [$"ConnectionStrings:{WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.ConnectionName}"] = "Endpoint=sb://test/;",
            [WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.QueueNameKey] = WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.QueueName,
            [WorkflowBuilderTopology.ClientConfig.SignalRBaseUrlKey] = "http://localhost:5000",
            [WorkflowBuilderTopology.ClientConfig.GrafanaBaseUrlKey] = "http://localhost:3000",
            ["Authentication:Enabled"] = authEnabled.ToString(),
            ["Authentication:Keycloak:Enabled"] = "true",
            ["Authentication:Keycloak:Authority"] = "https://localhost:8080/realms/workflow-builder",
            ["Authentication:Keycloak:Realm"] = "workflow-builder",
            ["Authentication:Keycloak:Audience"] = "workflow-builder-api",
            ["Authentication:Keycloak:SpaClientId"] = "workflow-builder-spa",
            ["Authentication:Keycloak:RequireHttpsMetadata"] = "false"
        };

    private sealed record ClientConfigResponse(
        string ApiBaseUrl,
        string SignalRBaseUrl,
        string GrafanaBaseUrl,
        ClientConfigAuthenticationResponse Authentication,
        ClientFeaturesResponse Features);

    private sealed record ClientFeaturesResponse(bool ConditionalBranchAuthoring);

    private sealed record ClientConfigAuthenticationResponse(
        bool Enabled,
        string Authority,
        string ClientId,
        string Scope,
        string Audience);

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previousValues = new(StringComparer.OrdinalIgnoreCase);

        public EnvironmentVariableScope(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var (key, value) in values)
            {
                var environmentKey = key.Replace(":", "__", StringComparison.Ordinal);
                _previousValues[environmentKey] = Environment.GetEnvironmentVariable(environmentKey);
                Environment.SetEnvironmentVariable(environmentKey, value);
            }
        }

        public void Dispose()
        {
            foreach (var (key, value) in _previousValues)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}
