using Backend.Plugins.Harness;

namespace WorkflowBuilder.IntegrationTests.Plugins;

[PluginJsonSerializable]
public sealed class RecoveryProbeRequest
{
    public Guid ProbeId { get; set; }
    public string Operation { get; set; } = "";
    public string? Address { get; set; }
    public string? Receipt { get; set; }
}

[PluginJsonSerializable]
public sealed class RecoveryProbeResult
{
    public string? Address { get; set; }
    public string? Receipt { get; set; }
}

/// <summary>Files stand in for an external provider. Each charge is observable even if the Engine loses its result.</summary>
public sealed class IntegrationRecoveryProbePlugin : IPlugin
{
    public required RecoveryProbeRequest Request { get; init; }
    public RecoveryProbeResult? Success { get; set; }

    public async Task Execute()
    {
        var directory = Path.Combine(Path.GetTempPath(), "workflow-builder-recovery-tests", Request.ProbeId.ToString("N"));
        switch (Request.Operation)
        {
            case "lookup":
                Success = new() { Address = await File.ReadAllTextAsync(Path.Combine(directory, "address")) };
                break;
            case "charge":
                var receipt = Guid.NewGuid().ToString("N");
                await File.WriteAllTextAsync(Path.Combine(directory, $"charge-{receipt}"), receipt);
                Success = new() { Receipt = receipt };
                break;
            case "deliver":
                if (!File.Exists(Path.Combine(directory, "online")))
                    throw new InvalidOperationException("Delivery service is offline.");
                await File.WriteAllLinesAsync(Path.Combine(directory, $"delivery-{Guid.NewGuid():N}"), [Request.Address!, Request.Receipt!]);
                Success = new() { Address = Request.Address, Receipt = Request.Receipt };
                break;
            default:
                throw new InvalidOperationException($"Unknown probe operation {Request.Operation}.");
        }
    }
}
