using System.Text.Json;
using Backend.Database.Relational;
using Backend.Database.Relational.Entities;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services.EngineManagement;

public sealed record ManagedSetting(string? Value, string? SecretReference);

public sealed record EnginePolicySnapshot(
    long Revision,
    IReadOnlyDictionary<string, ManagedSetting> Settings,
    IReadOnlySet<int> DisabledPlugins,
    IReadOnlySet<int> DisabledVersions)
{
    public bool IsDisabled(int taskDefinitionId, int taskVersionId) =>
        DisabledPlugins.Contains(taskDefinitionId) || DisabledVersions.Contains(taskVersionId);
}

public sealed class EnginePolicyConflictException() : Exception("The fleet policy has changed. Reload and retry.");

public interface IEnginePolicyReader
{
    Task<EnginePolicySnapshot> ReadAsync(CancellationToken cancellationToken);
}

public interface IEngineReplicaReporter
{
    Task ReportAsync(string engineId, long? appliedRevision, string? failureCode, CancellationToken cancellationToken,
        IReadOnlyCollection<int>? servingVersionIds = null);
}

public sealed class EngineManagementStore(BlueprintDbContext database) : IEnginePolicyReader, IEngineReplicaReporter
{
    // The fleet document is read atomically. Secret settings contain only immutable references.
    private sealed class PolicyDocument
    {
        public Dictionary<string, ManagedSetting> Settings { get; set; } = [];
        public HashSet<int> DisabledPlugins { get; set; } = [];
        public HashSet<int> DisabledVersions { get; set; } = [];
    }

    public async Task<EnginePolicySnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var row = await database.EngineFleetPolicies.AsNoTracking().SingleAsync(cancellationToken);
        var policy = ReadDocument(row);
        return new(row.Revision, policy.Settings, policy.DisabledPlugins, policy.DisabledVersions);
    }

    public Task<long> SetSettingAsync(
        long expectedRevision, string actor, string key, ManagedSetting? setting, CancellationToken cancellationToken) =>
        ChangeAsync(expectedRevision, actor, setting is null ? "RemoveOverride" : "SetOverride", key, policy =>
        {
            if (setting is null)
                policy.Settings.Remove(key);
            else
                policy.Settings[key] = setting;
        }, cancellationToken);

    public Task<long> SetEnabledAsync(
        long expectedRevision, string actor, int pluginId, int? versionId, bool enabled, CancellationToken cancellationToken)
    {
        var target = versionId is null ? $"plugins/{pluginId}" : $"plugins/{pluginId}/versions/{versionId}";
        return ChangeAsync(expectedRevision, actor, enabled ? "Enable" : "Disable", target, policy =>
        {
            var rules = versionId.HasValue ? policy.DisabledVersions : policy.DisabledPlugins;
            if (enabled)
                rules.Remove(versionId ?? pluginId);
            else
                rules.Add(versionId ?? pluginId);
        }, cancellationToken);
    }

    private async Task<long> ChangeAsync(
        long expectedRevision, string actor, string action, string target, Action<PolicyDocument> change,
        CancellationToken cancellationToken)
    {
        var current = await database.EngineFleetPolicies.AsNoTracking().SingleAsync(cancellationToken);
        if (current.Revision != expectedRevision) throw new EnginePolicyConflictException();
        var document = ReadDocument(current);
        change(document);
        var json = JsonSerializer.Serialize(document);
        var revision = checked(expectedRevision + 1);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var updated = await database.EngineFleetPolicies.Where(row => row.Id == 1 && row.Revision == expectedRevision)
            .ExecuteUpdateAsync(update => update
                .SetProperty(row => row.Revision, revision)
                .SetProperty(row => row.Document, json), cancellationToken);
        if (updated == 0) throw new EnginePolicyConflictException();
        database.EngineManagementChanges.Add(new()
        {
            Revision = revision, Actor = actor, Action = action, Target = target, ChangedAt = DateTimeOffset.UtcNow
        });
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return revision;
    }

    private static PolicyDocument ReadDocument(EngineFleetPolicy row) =>
        JsonSerializer.Deserialize<PolicyDocument>(row.Document) ?? throw new InvalidDataException("Invalid fleet policy.");

    public async Task ReportAsync(string engineId, long? appliedRevision, string? failureCode, CancellationToken cancellationToken,
        IReadOnlyCollection<int>? servingVersionIds = null)
    {
        var observation = await database.EnginePolicyObservations.FindAsync([engineId], cancellationToken);
        if (observation is null)
        {
            observation = new EnginePolicyObservation { EngineId = engineId };
            database.EnginePolicyObservations.Add(observation);
        }
        observation.AppliedRevision = appliedRevision;
        observation.LastSeenAt = DateTimeOffset.UtcNow;
        observation.FailureCode = failureCode;
        observation.ServingVersionIdsJson = JsonSerializer.Serialize(servingVersionIds ?? []);
        await database.SaveChangesAsync(cancellationToken);
    }
}
