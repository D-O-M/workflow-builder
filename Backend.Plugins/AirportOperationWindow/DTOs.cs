namespace AirportOperationWindow;

public sealed class AirportOperationWindowInput
{
    public string AirportCode { get; set; } = "";
    public string WindowStartIsoDate { get; set; } = "";
    public string WindowEndIsoDate { get; set; } = "";
}

public sealed class AirportOperationWindowResult
{
    public string AirportCode { get; set; } = "";
    public string WindowStartIsoDate { get; set; } = "";
    public string WindowEndIsoDate { get; set; } = "";
    public string WindowLabel { get; set; } = "";
}
