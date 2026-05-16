using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using Microsoft.Extensions.Configuration;

namespace Backend.Engine.Capabilities;

public interface ICommandCapabilityProbe
{
    Task<CommandProbeResult> ProbeAsync(
        string command,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public interface IEndpointCapabilityProbe
{
    Task<EndpointProbeResult> ProbeAsync(
        Uri endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public interface ICapabilityValidationService
{
    EngineCapabilityInventory GetInventory();

    Task<CapabilityValidationReport> ValidateAsync(
        IReadOnlyCollection<CapabilityRequirementDescriptor> capabilities,
        IReadOnlyCollection<ConfigurationRequirementDescriptor> configurationRequirements,
        CancellationToken cancellationToken);
}

public sealed record CommandProbeResult(
    bool Succeeded,
    int? ExitCode,
    string? Output,
    string? Error);

public sealed record EndpointProbeResult(
    bool Succeeded,
    string? Message);

public sealed class CapabilityValidationService : ICapabilityValidationService
{
    private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly IConfiguration _configuration;
    private readonly ICommandCapabilityProbe _commandProbe;
    private readonly IEndpointCapabilityProbe _endpointProbe;

    public CapabilityValidationService(
        IConfiguration configuration,
        ICommandCapabilityProbe commandProbe,
        IEndpointCapabilityProbe endpointProbe)
    {
        _configuration = configuration;
        _commandProbe = commandProbe;
        _endpointProbe = endpointProbe;
    }

    public EngineCapabilityInventory GetInventory() => new();

    public async Task<CapabilityValidationReport> ValidateAsync(
        IReadOnlyCollection<CapabilityRequirementDescriptor> capabilities,
        IReadOnlyCollection<ConfigurationRequirementDescriptor> configurationRequirements,
        CancellationToken cancellationToken)
    {
        var configurationByKey = configurationRequirements.ToDictionary(
            requirement => requirement.Key,
            StringComparer.Ordinal);
        var results = new List<CapabilityValidationResult>();

        foreach (var capability in capabilities.OrderBy(capability => capability.Id, StringComparer.Ordinal))
        {
            var configResult = ValidateConfiguration(capability, configurationByKey);
            if (configResult is not null)
            {
                results.Add(configResult);
                continue;
            }

            var result = capability.Kind switch
            {
                CapabilityKindNames.Software => await ValidateSoftwareAsync(capability, cancellationToken),
                CapabilityKindNames.Network => await ValidateNetworkAsync(capability, cancellationToken),
                CapabilityKindNames.Configuration => Satisfied(capability, "Required configuration is available."),
                _ => Unsupported(capability, $"Capability kind '{capability.Kind}' is not supported by this engine.")
            };

            results.Add(result);
        }

        return new CapabilityValidationReport
        {
            Results = results
        };
    }

    private CapabilityValidationResult? ValidateConfiguration(
        CapabilityRequirementDescriptor capability,
        IReadOnlyDictionary<string, ConfigurationRequirementDescriptor> configurationByKey)
    {
        foreach (var engineKey in capability.EngineConfigurationKeys)
        {
            if (!HasValue(engineKey))
            {
                return Missing(
                    capability,
                    $"Required engine configuration value '{engineKey}' was not supplied.");
            }
        }

        foreach (var key in capability.ConfigurationKeys)
        {
            if (!configurationByKey.TryGetValue(key, out var requirement))
            {
                return Failed(
                    capability,
                    $"Capability '{capability.Id}' references configuration key '{key}', but the manifest does not describe that key.");
            }

            if (requirement.Required && !HasValue(key))
            {
                return Missing(
                    capability,
                    $"Required plugin configuration value '{key}' was not supplied.");
            }
        }

        return null;
    }

    private async Task<CapabilityValidationResult> ValidateSoftwareAsync(
        CapabilityRequirementDescriptor capability,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(capability.Command))
        {
            return Unsupported(capability, "Software capability does not declare a command probe.");
        }

        var probe = await _commandProbe.ProbeAsync(
            capability.Command,
            capability.Arguments ?? "--version",
            DefaultProbeTimeout,
            cancellationToken);

        if (!probe.Succeeded)
        {
            return Missing(
                capability,
                string.IsNullOrWhiteSpace(probe.Error)
                    ? $"Command '{capability.Command}' is not available."
                    : probe.Error);
        }

        return Satisfied(capability, $"Command '{capability.Command}' is available.");
    }

    private async Task<CapabilityValidationResult> ValidateNetworkAsync(
        CapabilityRequirementDescriptor capability,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(capability.EndpointConfigKey))
        {
            return Satisfied(capability, "No endpoint probe is required.");
        }

        var endpointValue = _configuration[capability.EndpointConfigKey];
        if (string.IsNullOrWhiteSpace(endpointValue))
        {
            return Satisfied(capability, $"Endpoint configuration '{capability.EndpointConfigKey}' was not supplied; endpoint probe skipped.");
        }

        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint))
        {
            return Failed(
                capability,
                $"Endpoint configuration '{capability.EndpointConfigKey}' is not a valid absolute URI.");
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(1, capability.TimeoutSeconds ?? 5));
        var probe = await _endpointProbe.ProbeAsync(endpoint, timeout, cancellationToken);
        if (!probe.Succeeded)
        {
            return Missing(
                capability,
                $"Endpoint '{RedactUri(endpoint)}' is not reachable. {probe.Message}".Trim());
        }

        return Satisfied(capability, $"Endpoint '{RedactUri(endpoint)}' is reachable.");
    }

    private bool HasValue(string key)
    {
        var section = _configuration.GetSection(key);
        return section.Exists() && (section.Value is not null || section.GetChildren().Any());
    }

    private static CapabilityValidationResult Satisfied(
        CapabilityRequirementDescriptor capability,
        string message) =>
        Result(capability, CapabilityValidationStatus.Satisfied, message);

    private static CapabilityValidationResult Missing(
        CapabilityRequirementDescriptor capability,
        string message) =>
        Result(capability, CapabilityValidationStatus.Missing, message);

    private static CapabilityValidationResult Failed(
        CapabilityRequirementDescriptor capability,
        string message) =>
        Result(capability, CapabilityValidationStatus.Failed, message);

    private static CapabilityValidationResult Unsupported(
        CapabilityRequirementDescriptor capability,
        string message) =>
        Result(capability, CapabilityValidationStatus.Unsupported, message);

    private static CapabilityValidationResult Result(
        CapabilityRequirementDescriptor capability,
        CapabilityValidationStatus status,
        string message) =>
        new()
        {
            CapabilityId = capability.Id,
            DisplayName = capability.DisplayName,
            Kind = capability.Kind,
            Required = capability.Required,
            Status = status,
            Message = message,
            Remediation = capability.Remediation
        };

    private static string RedactUri(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            UserName = "",
            Password = ""
        };
        return builder.Uri.ToString();
    }
}

public sealed class ProcessCommandCapabilityProbe : ICommandCapabilityProbe
{
    public async Task<CommandProbeResult> ProbeAsync(
        string command,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            var output = await stdoutTask;
            var error = await stderrTask;

            return new CommandProbeResult(
                process.ExitCode == 0,
                process.ExitCode,
                output,
                process.ExitCode == 0 ? null : error);
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or OperationCanceledException)
        {
            return new CommandProbeResult(false, null, null, ex.Message);
        }
    }
}

public sealed class HttpEndpointCapabilityProbe : IEndpointCapabilityProbe
{
    private static readonly HttpClient Client = new();

    public async Task<EndpointProbeResult> ProbeAsync(
        Uri endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            using var request = new HttpRequestMessage(HttpMethod.Head, endpoint);
            using var response = await Client.SendAsync(request, timeoutCts.Token);
            return response.IsSuccessStatusCode || (int)response.StatusCode < 500
                ? new EndpointProbeResult(true, null)
                : new EndpointProbeResult(false, $"HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return new EndpointProbeResult(false, ex.Message);
        }
    }
}
