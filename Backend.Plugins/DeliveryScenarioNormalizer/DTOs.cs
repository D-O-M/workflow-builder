namespace DeliveryScenarioNormalizer;

public sealed class DeliveryScenarioInput
{
    public string ShipmentLabel { get; set; } = "";
    public string DestinationTown { get; set; } = "";
    public string DestinationCountryCode { get; set; } = "";
    public string SupplierCountryCode { get; set; } = "";
    public string ReceiverCountryCode { get; set; } = "";
    public string BaseCurrencyCode { get; set; } = "";
    public string QuoteCurrencyCode { get; set; } = "";
    public string WindowStartIsoDate { get; set; } = "";
    public string WindowEndIsoDate { get; set; } = "";
}

public sealed class DeliveryScenarioResult
{
    public string ShipmentLabel { get; set; } = "";
    public string DestinationTown { get; set; } = "";
    public string DestinationCountryCode { get; set; } = "";
    public string SupplierCountryCode { get; set; } = "";
    public string ReceiverCountryCode { get; set; } = "";
    public string BaseCurrencyCode { get; set; } = "";
    public string QuoteCurrencyCode { get; set; } = "";
    public string WindowStartIsoDate { get; set; } = "";
    public string WindowEndIsoDate { get; set; } = "";
    public string DestinationLabel { get; set; } = "";
}
