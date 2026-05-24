namespace TownScoreEmailer;

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class EmailSendInput
{
    public string Town { get; set; } = "";
    public int WeatherScore { get; set; }
    public double DemandAdjustment { get; set; }
    public int FinalScore { get; set; }
    public string Explanation { get; set; } = "";
    public string ForecastSummary { get; set; } = "";
    public string HolidaySummary { get; set; } = "";
    public string RecipientEmail { get; set; } = "";

    public string FromEmail { get; set; } = "";
    public string FromName { get; set; } = "Workflow Builder";

    public string SendGridApiKeyEnv { get; set; } = "SENDGRID_API_KEY";
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class EmailSendResult
{
    public bool Success { get; set; }
    public int StatusCode { get; set; }
    public string ProviderMessage { get; set; }
}
