namespace AirportOperationWindow;

public sealed class AirportOperationWindowInput
{
    public string AirportCode { get; set; } = "";
    public DateOnly WindowStartIsoDate { get; set; }
    public DateOnly WindowEndIsoDate { get; set; }
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class AirportOperationWindowResult
{
    public string AirportCode { get; set; } = "";
    public DateOnly WindowStartIsoDate { get; set; }
    public DateOnly WindowEndIsoDate { get; set; }
    public string WindowLabel { get; set; } = "";
}
