using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Backend.Plugins.Harness;

namespace TownScoreEmailer;

public sealed class TownEmailPlugin : IPlugin
{
    public required EmailSendInput Input { get; init; }
    public EmailSendResult? Sent { get; set; }

    private static readonly HttpClient _http = CreateHttpClient();

    public async Task Execute()
    {
        if (string.IsNullOrWhiteSpace(Input.RecipientEmail))
            throw new InvalidDataException("RecipientEmail is required.");
        if (string.IsNullOrWhiteSpace(Input.FromEmail))
            throw new InvalidDataException("FromEmail is required.");

        var envName = string.IsNullOrWhiteSpace(Input.SendGridApiKeyEnv) ? "SENDGRID_API_KEY" : Input.SendGridApiKeyEnv;
        var apiKey = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidDataException($"SendGrid API key missing. Set env var '{envName}'.");

        var subject = $"Town score: {Input.Town} = {Input.FinalScore}/100";
        var body = @$"""Town: {Input.Town}

Final score: {Input.FinalScore}/100

How scoring works:
{Input.Explanation}

Forecast summary:
{Input.ForecastSummary}

Holiday effect:
{Input.HolidaySummary}
""";

        var payload = new SendGridMailSendRequest
        {
            From = new EmailAddress { Email = Input.FromEmail, Name = Input.FromName },
            Personalizations =
            [
                new Personalization
                {
                    Tos = [ new EmailAddress { Email = Input.RecipientEmail } ],
                    Subject = subject
                }
            ],
            Content =
            [
                new ContentItem { Type = "text/plain", Value = body }
            ]
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.sendgrid.com/v3/mail/send")
        {
            Content = JsonContent.Create(payload)
        };

        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        var resp = await _http.SendAsync(req);
        var ok = (int)resp.StatusCode >= 200 && (int)resp.StatusCode < 300;

        Sent = new EmailSendResult
        {
            Success = ok,
            StatusCode = (int)resp.StatusCode,
            ProviderMessage = ok ? "Accepted" : await resp.Content.ReadAsStringAsync()
        };

        if (!ok)
            throw new InvalidOperationException($"SendGrid send failed: {(int)resp.StatusCode} {Sent.ProviderMessage}");
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "workflow-builder/1.0 (email; contact=admin@example.invalid)");
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    private sealed class SendGridMailSendRequest
    {
        [JsonPropertyName("personalizations")] public List<Personalization> Personalizations { get; set; } = new();
        [JsonPropertyName("from")] public EmailAddress From { get; set; } = new();
        [JsonPropertyName("content")] public List<ContentItem> Content { get; set; } = new();
    }

    private sealed class Personalization
    {
        [JsonPropertyName("to")] public List<EmailAddress> Tos { get; set; } = new();
        [JsonPropertyName("subject")] public string Subject { get; set; } = "";
    }

    private sealed class EmailAddress
    {
        [JsonPropertyName("email")] public string Email { get; set; } = "";
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    private sealed class ContentItem
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "text/plain";
        [JsonPropertyName("value")] public string Value { get; set; } = "";
    }
}
