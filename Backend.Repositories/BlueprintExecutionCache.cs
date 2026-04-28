using Backend.Interfaces.Repositories;
using Backend.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Repositories;

public sealed class BlueprintExecutionCache : IBlueprintExecutionCache
{
    private readonly IMemoryCache _cache;
    private readonly IServiceScopeFactory _scopeFactory;

    public BlueprintExecutionCache(
        IMemoryCache cache,
        IServiceScopeFactory scopeFactory)
    {
        _cache = cache;
        _scopeFactory = scopeFactory;
    }

    public Task<ExecutableBlueprint> GetExecutableBlueprintAsync(
        int blueprintVersionId,
        CancellationToken cancellationToken) =>
        _cache.GetOrCreateAsync(
            CacheKey(blueprintVersionId),
            async entry =>
            {
                entry.Priority = CacheItemPriority.High;
                await using var scope = _scopeFactory.CreateAsyncScope();
                var blueprintRepository = scope.ServiceProvider.GetRequiredService<IBlueprintRepository>();
                return await blueprintRepository.GetFullBlueprintById(
                    blueprintVersionId,
                    cancellationToken);
            })!;

    private static string CacheKey(int blueprintVersionId) =>
        $"blueprint-execution:{blueprintVersionId}";
}
