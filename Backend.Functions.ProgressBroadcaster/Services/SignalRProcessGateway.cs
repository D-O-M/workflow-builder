using System.Net.Http.Json;
using Backend.Models.Realtime;
using Microsoft.Extensions.Logging;

namespace Backend.Functions.ProgressBroadcaster.Services;

public sealed class SignalRProcessGateway
{
    public const string HttpClientName = "SignalRInternal";
    private static readonly TimeSpan SubscriptionSnapshotTtl = TimeSpan.FromSeconds(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SignalRProcessGateway> _logger;
    private readonly object _gate = new();
    private ProcessSubscriptionSnapshotViewModel? _cachedSnapshot;
    private DateTimeOffset _cachedSnapshotExpiresAt = DateTimeOffset.MinValue;

    public SignalRProcessGateway(
        IHttpClientFactory httpClientFactory,
        ILogger<SignalRProcessGateway> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<ProcessSubscriptionSnapshotViewModel> GetActiveSubscriptionsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_cachedSnapshot is not null &&
                DateTimeOffset.UtcNow < _cachedSnapshotExpiresAt &&
                HasActiveSubscriptions(_cachedSnapshot))
            {
                return Clone(_cachedSnapshot);
            }
        }

        try
        {
            var snapshot = await _httpClientFactory
                .CreateClient(HttpClientName)
                .GetFromJsonAsync<ProcessSubscriptionSnapshotViewModel>(
                    "internal/subscriptions/snapshot",
                    cancellationToken)
                ?? new ProcessSubscriptionSnapshotViewModel();

            lock (_gate)
            {
                _cachedSnapshot = Clone(snapshot);
                _cachedSnapshotExpiresAt = DateTimeOffset.UtcNow.Add(SubscriptionSnapshotTtl);
            }

            return snapshot;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to retrieve active process subscriptions from Backend.SignalR.");

            lock (_gate)
            {
                return _cachedSnapshot is not null
                    ? Clone(_cachedSnapshot)
                    : new ProcessSubscriptionSnapshotViewModel();
            }
        }
    }

    public async Task PublishAsync(ProcessRealtimeBatch batch, CancellationToken cancellationToken)
    {
        if (batch.Summaries.Count == 0 && batch.NodeUpdates.Count == 0)
        {
            return;
        }

        try
        {
            using var response = await _httpClientFactory
                .CreateClient(HttpClientName)
                .PostAsJsonAsync("internal/process-events", batch, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Backend.SignalR rejected process progress updates with status code {StatusCode}.",
                    response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to publish process progress updates to Backend.SignalR.");
        }
    }

    private static ProcessSubscriptionSnapshotViewModel Clone(ProcessSubscriptionSnapshotViewModel snapshot) =>
        new()
        {
            BlueprintDefinitionIds = [.. snapshot.BlueprintDefinitionIds],
            ProcessIds = [.. snapshot.ProcessIds],
        };

    private static bool HasActiveSubscriptions(ProcessSubscriptionSnapshotViewModel snapshot) =>
        snapshot.BlueprintDefinitionIds.Count > 0 || snapshot.ProcessIds.Count > 0;
}
