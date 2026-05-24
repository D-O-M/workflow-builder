namespace EcbExchangeRateSnapshot;

public sealed class EcbExchangeRateInput
{
    public string BaseCurrencyCode { get; set; } = "";
    public string QuoteCurrencyCode { get; set; } = "";
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class EcbExchangeRateResult
{
    public string BaseCurrencyCode { get; set; } = "";
    public string QuoteCurrencyCode { get; set; } = "";
    public double ExchangeRate { get; set; }
    public double InverseRate { get; set; }
    public string Summary { get; set; } = "";
}
