namespace DeliveryScenarioNormalizer;

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class DeliveryScenarioInput
{
    public string ShipmentLabel { get; set; } = "";
    public string DestinationTown { get; set; } = "";
    public string DestinationCountryCode { get; set; } = "";
    public string SupplierCountryCode { get; set; } = "";
    public string ReceiverCountryCode { get; set; } = "";
    public string BaseCurrencyCode { get; set; } = "";
    public string QuoteCurrencyCode { get; set; } = "";
    public DateOnly WindowStartIsoDate { get; set; }
    public DateOnly WindowEndIsoDate { get; set; }
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class DeliveryScenarioResult
{
    public string ShipmentLabel { get; set; } = "";
    public string DestinationTown { get; set; } = "";
    public string DestinationCountryCode { get; set; } = "";
    public string SupplierCountryCode { get; set; } = "";
    public string ReceiverCountryCode { get; set; } = "";
    public string BaseCurrencyCode { get; set; } = "";
    public string QuoteCurrencyCode { get; set; } = "";
    public DateOnly WindowStartIsoDate { get; set; }
    public DateOnly WindowEndIsoDate { get; set; }
    public string DestinationLabel { get; set; } = "";
}
