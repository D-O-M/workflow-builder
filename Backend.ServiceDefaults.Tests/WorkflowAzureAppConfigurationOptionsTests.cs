using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
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
}
