using Backend.Database.State.Objects;

namespace Backend.Interfaces.Repositories;

public interface IPluginTaskVersionRuntimeStatusStore
{
    Task UpsertAsync(
        PluginTaskVersionRuntimeStatusUpdate update,
        CancellationToken cancellationToken);

    Task RefreshLastSeenAsync(
        string engineInstanceId,
        IReadOnlyCollection<int> taskVersionIds,
        DateTimeOffset refreshedAt,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PluginTaskVersionRuntimeStatusRecord>> GetFreshByTaskVersionIdsAsync(
        IReadOnlyCollection<int> taskVersionIds,
        DateTimeOffset freshAfter,
        CancellationToken cancellationToken);
}

public static class PluginTaskVersionRuntimeStatusIds
{
    public static string Create(int taskVersionId, string engineInstanceId) =>
        $"{taskVersionId}:{NormalizeEngineInstanceId(engineInstanceId)}";

    private static string NormalizeEngineInstanceId(string engineInstanceId) =>
        string.IsNullOrWhiteSpace(engineInstanceId)
            ? "unknown"
            : engineInstanceId.Trim().ToLowerInvariant();
}
