using System.Collections.Concurrent;
using Backend.Database.Relational;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Functions.ProgressBroadcaster.Services;

public sealed class BlueprintDefinitionLookupCache
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<int, int> _blueprintDefinitionIdsByVersionId = new();

    public BlueprintDefinitionLookupCache(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<Dictionary<int, int>> GetBlueprintDefinitionIdsAsync(
        IEnumerable<int> blueprintVersionIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, int>();
        var missingIds = new List<int>();

        foreach (var blueprintVersionId in blueprintVersionIds
            .Where(id => id > 0)
            .Distinct())
        {
            if (_blueprintDefinitionIdsByVersionId.TryGetValue(blueprintVersionId, out var cachedBlueprintDefinitionId))
            {
                result[blueprintVersionId] = cachedBlueprintDefinitionId;
            }
            else
            {
                missingIds.Add(blueprintVersionId);
            }
        }

        if (missingIds.Count == 0)
        {
            return result;
        }

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BlueprintDbContext>();
        var resolvedIds = await dbContext.BlueprintVersions
            .AsNoTracking()
            .Where(version => missingIds.Contains(version.Id))
            .Select(version => new
            {
                version.Id,
                version.BlueprintDefinitionId,
            })
            .ToListAsync(cancellationToken);

        foreach (var resolvedId in resolvedIds)
        {
            _blueprintDefinitionIdsByVersionId[resolvedId.Id] = resolvedId.BlueprintDefinitionId;
            result[resolvedId.Id] = resolvedId.BlueprintDefinitionId;
        }

        return result;
    }
}
