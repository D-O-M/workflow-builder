using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Backend.Topology;
using Shouldly;

namespace Backend.ServiceDefaults.Tests;

public sealed class WorkflowAzureAppConfigurationOptionsTests
{
    [Test]
    public void Azure_app_configuration_is_disabled_by_default()
    {
        var options = new ConfigurationBuilder()
            .AddInMemoryCollection()
            .Build()
            .GetSection(WorkflowAzureAppConfigurationOptions.SectionName)
            .Get<WorkflowAzureAppConfigurationOptions>()
            ?? new WorkflowAzureAppConfigurationOptions();

        options.Enabled.ShouldBeFalse();
        options.IsUsable.ShouldBeFalse();
    }

    [Test]
    public void Azure_app_configuration_binds_endpoint_label_and_identity()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureAppConfiguration:Enabled"] = "true",
                ["AzureAppConfiguration:Endpoint"] = "https://demo.azconfig.io",
                ["AzureAppConfiguration:Label"] = "demo",
                ["AzureAppConfiguration:ManagedIdentityClientId"] = "identity-client-id",
                ["AzureAppConfiguration:IncludeNullLabel"] = "false"
            })
            .Build();

        var options = configuration
            .GetSection(WorkflowAzureAppConfigurationOptions.SectionName)
            .Get<WorkflowAzureAppConfigurationOptions>();

        options.ShouldNotBeNull();
        options.IsUsable.ShouldBeTrue();
        options.Endpoint.ShouldBe("https://demo.azconfig.io");
        options.Label.ShouldBe("demo");
        options.ManagedIdentityClientId.ShouldBe("identity-client-id");
        options.IncludeNullLabel.ShouldBeFalse();
    }

    [Test]
    public void Service_defaults_readd_environment_variables_after_azure_app_configuration()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "Backend.ServiceDefaults",
            "Extensions.cs"));
        var source = File.ReadAllText(sourcePath);

        var appConfigurationIndex = source.IndexOf(
            "AddAzureAppConfiguration(appConfig",
            StringComparison.Ordinal);
        var finalEnvironmentVariablesIndex = source.LastIndexOf(
            "AddEnvironmentVariables()",
            StringComparison.Ordinal);

        appConfigurationIndex.ShouldBeGreaterThanOrEqualTo(0);
        finalEnvironmentVariablesIndex.ShouldBeGreaterThan(appConfigurationIndex);
    }

    [Test]
    public void Topology_validation_reads_environment_variables_readded_by_service_defaults()
    {
        using var environment = new ScopedEnvironmentVariables(new Dictionary<string, string?>
        {
            ["AzureCosmos__ConnectionName"] = "enginedb",
            ["AzureCosmos__DatabaseName"] = WorkflowBuilderTopology.Cosmos.DefaultDatabaseName,
            ["ConnectionStrings__enginedb"] = "AccountEndpoint=https://workflow-builder.documents.azure.com:443/;AccountKey=demo;",
            ["AzureServiceBus__Queues__ProcessStepQueue__ConnectionName"] = "process-step-queue",
            ["AzureServiceBus__Queues__ProcessStepQueue__Name"] = WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.QueueName,
            ["ConnectionStrings__process-step-queue"] = "Endpoint=sb://workflow-builder.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=demo"
        });
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Production
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [WorkflowBuilderTopology.Cosmos.DatabaseNameKey] = "wrongdb",
            [WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.QueueNameKey] = "wrongqueue"
        });

        Should.Throw<WorkflowBuilderTopologyValidationException>(
            () => WorkflowBuilderTopologyValidator.ValidateDispatcher(builder.Configuration));

        builder.AddServiceDefaults();

        Should.NotThrow(() => WorkflowBuilderTopologyValidator.ValidateDispatcher(builder.Configuration));
    }

    private sealed class ScopedEnvironmentVariables : IDisposable
    {
        private readonly Dictionary<string, string?> _previousValues = new(StringComparer.OrdinalIgnoreCase);

        public ScopedEnvironmentVariables(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var (key, value) in values)
            {
                _previousValues[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, value);
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
