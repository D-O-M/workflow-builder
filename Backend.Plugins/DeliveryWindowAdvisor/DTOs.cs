namespace DeliveryWindowAdvisor;

public sealed class DeliveryWindowAdvisorInput
{
    public string ShipmentLabel { get; set; } = "";
    public string DestinationLabel { get; set; } = "";
    public string BaseCurrencyCode { get; set; } = "";
    public string QuoteCurrencyCode { get; set; } = "";
    public int WeatherRiskScore { get; set; }
    public string WeatherSummary { get; set; } = "";
    public double AverageDaylightHours { get; set; }
    public string DaylightSummary { get; set; } = "";
    public int SupplierHolidayPressureScore { get; set; }
    public string SupplierHolidaySummary { get; set; } = "";
    public int ReceiverHolidayPressureScore { get; set; }
    public string ReceiverHolidaySummary { get; set; } = "";
    public double ExchangeRate { get; set; }
    public string FxSummary { get; set; } = "";
    public double TargetExchangeRate { get; set; }
    public double SensitivityMultiplier { get; set; }
    public int GoThreshold { get; set; }
    public int WatchThreshold { get; set; }
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class DeliveryWindowAdvisorResult
{
    public string ShipmentLabel { get; set; } = "";
    public string DecisionBand { get; set; } = "";
    public int DecisionScore { get; set; }
    public string RecommendedAction { get; set; } = "";
    public string Summary { get; set; } = "";
}
