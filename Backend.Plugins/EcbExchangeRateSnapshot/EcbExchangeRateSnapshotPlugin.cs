using System.Globalization;
using Backend.Plugins.Harness;

namespace EcbExchangeRateSnapshot;

public sealed class EcbExchangeRateSnapshotPlugin : IPlugin
{
    private readonly HttpClient _httpClient;

    public required EcbExchangeRateInput Input { get; init; }
    public EcbExchangeRateResult Rate { get; set; } = default!;

    public EcbExchangeRateSnapshotPlugin()
        : this(CreateHttpClient())
    {
    }

    public EcbExchangeRateSnapshotPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task Execute()
    {
        var baseCurrency = Input.BaseCurrencyCode.Trim().ToUpperInvariant();
        var quoteCurrency = Input.QuoteCurrencyCode.Trim().ToUpperInvariant();

        if (string.IsNullOrWhiteSpace(baseCurrency) || string.IsNullOrWhiteSpace(quoteCurrency))
        {
            throw new InvalidDataException("BaseCurrencyCode and QuoteCurrencyCode are required.");
        }

        double rate;
        if (string.Equals(baseCurrency, quoteCurrency, StringComparison.Ordinal))
        {
            rate = 1.0;
        }
        else
        {
            var eurBase = await LoadEuroToCurrencyRateAsync(baseCurrency);
            var eurQuote = await LoadEuroToCurrencyRateAsync(quoteCurrency);
            rate = eurQuote / eurBase;
        }

        Rate = new EcbExchangeRateResult
        {
            BaseCurrencyCode = baseCurrency,
            QuoteCurrencyCode = quoteCurrency,
            ExchangeRate = Math.Round(rate, 6),
            InverseRate = Math.Round(1.0 / rate, 6),
            Summary = $"1 {baseCurrency} = {Math.Round(rate, 4)} {quoteCurrency} from the latest ECB reference snapshot."
        };
    }

    private async Task<double> LoadEuroToCurrencyRateAsync(string currencyCode)
    {
        if (string.Equals(currencyCode, "EUR", StringComparison.Ordinal))
        {
            return 1.0;
        }

        var url = $"https://data-api.ecb.europa.eu/service/data/EXR/D.{currencyCode}.EUR.SP00.A?lastNObservations=1&format=csvdata";
        var csv = await _httpClient.GetStringAsync(url);
        var lines = csv.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
        {
            throw new InvalidDataException($"ECB response for {currencyCode} did not contain an observation row.");
        }

        var header = lines[0].Split(',');
        var row = lines[^1].Split(',');
        var valueIndex = Array.FindIndex(header, column => string.Equals(column, "OBS_VALUE", StringComparison.OrdinalIgnoreCase));
        if (valueIndex < 0 || valueIndex >= row.Length || !double.TryParse(row[valueIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
        {
            throw new InvalidDataException($"ECB response for {currencyCode} did not contain a valid OBS_VALUE.");
        }

        return rate;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("workflow-builder/1.0 (ecb-rate; contact=admin@example.invalid)");
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }
}
