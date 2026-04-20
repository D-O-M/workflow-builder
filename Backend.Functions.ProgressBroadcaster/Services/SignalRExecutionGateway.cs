using System.Net.Http.Json;
using Backend.Models.Realtime;
using Microsoft.Extensions.Logging;

namespace Backend.Functions.ProgressBroadcaster.Services;

public sealed class SignalRExecutionGateway
{
    public const string HttpClientName = "SignalRInternal";
    private static readonly TimeSpan SubscriptionSnapshotTtl = TimeSpan.FromSeconds(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SignalRExecutionGateway> _logger;
    private readonly object _gate = new();
    private ExecutionSubscriptionSnapshotViewModel? _cachedSnapshot;
    private DateTimeOffset _cachedSnapshotExpiresAt = DateTimeOffset.MinValue;

    public SignalRExecutionGateway(
        IHttpClientFactory httpClientFactory,
        ILogger<SignalRExecutionGateway> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<ExecutionSubscriptionSnapshotViewModel> GetActiveSubscriptionsAsync(CancellationToken cancellationToken)
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
                .GetFromJsonAsync<ExecutionSubscriptionSnapshotViewModel>(
                    "internal/subscriptions/snapshot",
                    cancellationToken)
                ?? new ExecutionSubscriptionSnapshotViewModel();

            lock (_gate)
            {
                _cachedSnapshot = Clone(snapshot);
                _cachedSnapshotExpiresAt = DateTimeOffset.UtcNow.Add(SubscriptionSnapshotTtl);
            }

            return snapshot;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to retrieve active execution subscriptions from Backend.SignalR.");

            lock (_gate)
            {
                return _cachedSnapshot is not null
                    ? Clone(_cachedSnapshot)
                    : new ExecutionSubscriptionSnapshotViewModel();
            }
        }
    }

    public async Task PublishAsync(ExecutionRealtimeBatch batch, CancellationToken cancellationToken)
    {
        if (batch.Summaries.Count == 0 && batch.NodeUpdates.Count == 0)
        {
            return;
        }

        try
        {
            using var response = await _httpClientFactory
                .CreateClient(HttpClientName)
                .PostAsJsonAsync("internal/execution-events", batch, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Backend.SignalR rejected execution progress updates with status code {StatusCode}.",
                    response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to publish execution progress updates to Backend.SignalR.");
        }
    }

    private static ExecutionSubscriptionSnapshotViewModel Clone(ExecutionSubscriptionSnapshotViewModel snapshot) =>
        new()
        {
            BlueprintDefinitionIds = [.. snapshot.BlueprintDefinitionIds],
            WorkflowInstanceIds = [.. snapshot.WorkflowInstanceIds],
        };

    private static bool HasActiveSubscriptions(ExecutionSubscriptionSnapshotViewModel snapshot) =>
        snapshot.BlueprintDefinitionIds.Count > 0 || snapshot.WorkflowInstanceIds.Count > 0;
}
