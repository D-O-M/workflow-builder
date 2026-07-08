using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backend.BlueprintSeeder;

internal sealed class BlueprintSeederAccessTokenProvider
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;
    private readonly BlueprintSeedingOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BlueprintSeederAccessTokenProvider> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private string? _accessToken;
    private DateTimeOffset _expiresAt;

    public BlueprintSeederAccessTokenProvider(
        HttpClient httpClient,
        IOptions<BlueprintSeedingOptions> options,
        TimeProvider timeProvider,
        ILogger<BlueprintSeederAccessTokenProvider> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var auth = _options.Authentication;
        if (!auth.Enabled)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(_accessToken) &&
            _expiresAt - RefreshSkew > _timeProvider.GetUtcNow())
        {
            return _accessToken;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_accessToken) &&
                _expiresAt - RefreshSkew > _timeProvider.GetUtcNow())
            {
                return _accessToken;
            }

            var authority = RequireConfigured(auth.Authority, "BlueprintSeeding:Authentication:Authority")
                .TrimEnd('/');
            var clientSecret = RequireConfigured(auth.ClientSecret, "BlueprintSeeding:Authentication:ClientSecret");

            using var form = new FormUrlEncodedContent(BuildTokenRequest(auth.ClientId, clientSecret, auth.Scope));
            using var response = await _httpClient.PostAsync(
                $"{authority}/protocol/openid-connect/token",
                form,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException(
                    $"Blueprint seeder authentication failed with HTTP {(int)response.StatusCode}: {body}");
            }

            var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
            if (tokenResponse is null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
            {
                throw new InvalidOperationException("Blueprint seeder authentication response did not contain an access token.");
            }

            _accessToken = tokenResponse.AccessToken;
            _expiresAt = _timeProvider.GetUtcNow().AddSeconds(Math.Max(0, tokenResponse.ExpiresIn));
            _logger.LogDebug(
                "Acquired blueprint seeder access token for client {ClientId}; expires at {ExpiresAt}.",
                auth.ClientId,
                _expiresAt);
            return _accessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static Dictionary<string, string> BuildTokenRequest(
        string clientId,
        string clientSecret,
        string? scope)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret
        };

        if (!string.IsNullOrWhiteSpace(scope))
        {
            values["scope"] = scope;
        }

        return values;
    }

    private static string RequireConfigured(string? value, string key) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{key} is required when blueprint seeder authentication is enabled.")
            : value;

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }
    }
}
