using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Backend.API.Observability;
using Backend.Database.Relational;
using Backend.Database.Relational.Entities;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Models.ViewModels;
using Backend.Repositories;
using Backend.Repositories.Processes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Backend.API.Services;

public sealed class ProcessQueryService
{
    private const int DefaultRankingWindowHours = 24;
    private const int ActivityInspectionPageSize = 25;
    private const int ActivityInspectionCapPerVersion = 100;
    private const string BlueprintProcessActivityCacheKey = "process-activity-index-v1";
    private static readonly JsonSerializerOptions CursorSerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ProcessProjectionService _projectionService;
    private readonly BlueprintDbContext _dbContext;
    private readonly IProcessInstanceRepository<Dictionary<string, JsonElement>> _processInstanceRepository;
    private readonly IMemoryCache _memoryCache;
    private readonly ObservabilityQueryService _observabilityQueryService;

    public ProcessQueryService(
        ProcessProjectionService projectionService,
        BlueprintDbContext dbContext,
        IProcessInstanceRepository<Dictionary<string, JsonElement>> processInstanceRepository,
        IMemoryCache memoryCache,
        ObservabilityQueryService observabilityQueryService)
    {
        _projectionService = projectionService;
        _dbContext = dbContext;
        _processInstanceRepository = processInstanceRepository;
        _memoryCache = memoryCache;
        _observabilityQueryService = observabilityQueryService;
    }

    public async Task<List<ProcessSummaryViewModel>> GetProcessesForBlueprintDefinitionAsync(
        int blueprintDefinitionId,
        CancellationToken cancellationToken) =>
        await _projectionService.GetProcessesForBlueprintDefinitionAsync(
            blueprintDefinitionId,
            cancellationToken);

    public async Task<ProcessDetailViewModel> GetProcessDetailAsync(
        int blueprintDefinitionId,
        Guid processId,
        CancellationToken cancellationToken) =>
        await _projectionService.GetProcessDetailAsync(
            blueprintDefinitionId,
            processId,
            cancellationToken);

    public async Task<ProcessStepDetailViewModel> GetProcessStepDetailAsync(
        int blueprintDefinitionId,
        Guid processId,
        Guid blueprintNodeId,
        CancellationToken cancellationToken) =>
        await _projectionService.GetProcessStepDetailAsync(
            blueprintDefinitionId,
            processId,
            blueprintNodeId,
            cancellationToken);

    public async Task<ProcessStepLogBatchViewModel> GetProcessStepLogsAsync(
        int blueprintDefinitionId,
        Guid processId,
        Guid blueprintNodeId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var runtimeProcessStepId = await _projectionService.GetRuntimeProcessStepIdAsync(
            blueprintDefinitionId,
            processId,
            blueprintNodeId,
            cancellationToken);

        if (!runtimeProcessStepId.HasValue)
        {
            return new ProcessStepLogBatchViewModel
            {
                Entries = [],
                NextCursor = cursor,
            };
        }

        return await _observabilityQueryService.GetProcessStepLogsByRuntimeIdAsync(
            processId,
            runtimeProcessStepId.Value,
            from,
            to,
            limit,
            cursor,
            cancellationToken);
    }

    public async Task<BlueprintProcessActivityPageViewModel> GetProcessBlueprintActivitiesAsync(
        string? query,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 50);

        var activities = await GetCachedProcessActivityIndexAsync(cancellationToken);
        IEnumerable<BlueprintProcessActivityViewModel> filtered = activities;

        if (!string.IsNullOrWhiteSpace(query))
        {
            var normalizedQuery = query.Trim();
            filtered = filtered.Where(activity =>
                activity.BlueprintName.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase));
        }

        var filteredList = filtered.ToList();
        var totalCount = filteredList.Count;
        var items = filteredList
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new BlueprintProcessActivityPageViewModel
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            HasMore = page * pageSize < totalCount,
            RankingWindowHours = DefaultRankingWindowHours,
        };
    }

    public async Task<BlueprintProcessHistoryPageViewModel> GetProcessHistoryAsync(
        int blueprintDefinitionId,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 100);

        var definition = await _dbContext.BlueprintDefinitions
            .Include(candidate => candidate.Versions)
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == blueprintDefinitionId, cancellationToken);

        if (definition is null)
        {
            throw new EntityNotFoundException<BlueprintViewModel>(blueprintDefinitionId);
        }

        if (definition.Versions.Count == 0)
        {
            return new BlueprintProcessHistoryPageViewModel
            {
                Items = [],
                HasMore = false,
                NextCursor = null,
            };
        }

        var partitionStates = CreatePartitionStates(definition, cursor);
        var versionsById = definition.Versions.ToDictionary(version => version.Id);
        var items = new List<BlueprintProcessHistoryEntryViewModel>(limit);

        while (items.Count < limit)
        {
            await EnsurePartitionBuffersAsync(partitionStates, limit, cancellationToken);

            var nextPartition = partitionStates
                .Where(partition => partition.Buffer.Count > 0)
                .OrderByDescending(partition => partition.Buffer[0].CreatedAt)
                .ThenByDescending(partition => partition.Buffer[0].ProcessId, StringComparer.Ordinal)
                .FirstOrDefault();

            if (nextPartition is null)
            {
                break;
            }

            var nextItem = nextPartition.Buffer[0];
            nextPartition.Buffer.RemoveAt(0);

            var version = versionsById[nextPartition.BlueprintVersionId];
            items.Add(new BlueprintProcessHistoryEntryViewModel
            {
                ProcessId = Guid.Parse(nextItem.ProcessId),
                BlueprintDefinitionId = definition.Id,
                BlueprintName = definition.DisplayName,
                BlueprintVersionId = version.Id,
                BlueprintVersionNumber = version.Version,
                Status = nextItem.Status.ToString(),
                CreatedAt = nextItem.CreatedAt,
                UpdatedAt = nextItem.UpdatedAt,
            });
        }

        var hasMore = partitionStates.Any(partition => partition.Buffer.Count > 0 || !partition.IsExhausted);

        return new BlueprintProcessHistoryPageViewModel
        {
            Items = items,
            HasMore = hasMore,
            NextCursor = hasMore ? EncodeCursor(partitionStates) : null,
        };
    }

    private async Task<IReadOnlyList<BlueprintProcessActivityViewModel>> GetCachedProcessActivityIndexAsync(
        CancellationToken cancellationToken)
    {
        var cached = await _memoryCache.GetOrCreateAsync(
            BlueprintProcessActivityCacheKey,
            async cacheEntry =>
            {
                cacheEntry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
                return await BuildProcessActivityIndexAsync(cancellationToken);
            });

        return cached ?? [];
    }

    private async Task<IReadOnlyList<BlueprintProcessActivityViewModel>> BuildProcessActivityIndexAsync(
        CancellationToken cancellationToken)
    {
        var definitions = await _dbContext.BlueprintDefinitions
            .Include(definition => definition.Versions)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (definitions.Count == 0)
        {
            return [];
        }

        var rankingWindowStart = DateTimeOffset.UtcNow.AddHours(-DefaultRankingWindowHours);
        var versionResults = new ConcurrentDictionary<int, BlueprintVersionProcessActivity>();
        using var throttler = new SemaphoreSlim(8);

        var tasks = definitions
            .SelectMany(definition => definition.Versions)
            .Select(async version =>
            {
                await throttler.WaitAsync(cancellationToken);
                try
                {
                    var latestProcess = await _processInstanceRepository.GetLatestProcessStateForBlueprint(
                        version.Id,
                        cancellationToken);

                    if (latestProcess is null)
                    {
                        versionResults[version.Id] = new BlueprintVersionProcessActivity(null, 0);
                        return;
                    }

                    var recentProcessCount = await EstimateRecentProcessCountAsync(
                        version.Id,
                        rankingWindowStart,
                        cancellationToken);

                    versionResults[version.Id] = new BlueprintVersionProcessActivity(
                        latestProcess.CreatedAt,
                        recentProcessCount);
                }
                finally
                {
                    throttler.Release();
                }
            });

        await Task.WhenAll(tasks);

        return definitions
            .Select(definition =>
            {
                var results = definition.Versions
                    .Select(version => versionResults.TryGetValue(version.Id, out var result) ? result : null)
                    .Where(result => result is not null)
                    .Cast<BlueprintVersionProcessActivity>()
                    .ToList();

                var latestProcessAt = results
                    .Where(result => result.LatestProcessAt.HasValue)
                    .Select(result => result.LatestProcessAt!.Value)
                    .DefaultIfEmpty()
                    .Max();

                if (latestProcessAt == default)
                {
                    return null;
                }

                return new BlueprintProcessActivityViewModel
                {
                    BlueprintDefinitionId = definition.Id,
                    BlueprintName = definition.DisplayName,
                    RecentProcessCount = results.Sum(result => result.RecentProcessCount),
                    LatestProcessAt = latestProcessAt,
                };
            })
            .Where(activity => activity is not null)
            .Cast<BlueprintProcessActivityViewModel>()
            .OrderByDescending(activity => activity.RecentProcessCount)
            .ThenByDescending(activity => activity.LatestProcessAt)
            .ThenBy(activity => activity.BlueprintName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<int> EstimateRecentProcessCountAsync(
        int blueprintVersionId,
        DateTimeOffset rankingWindowStart,
        CancellationToken cancellationToken)
    {
        var nextCursor = (string?)null;
        var recentProcessCount = 0;
        var inspectedCount = 0;

        while (inspectedCount < ActivityInspectionCapPerVersion)
        {
            var pageSize = Math.Min(ActivityInspectionPageSize, ActivityInspectionCapPerVersion - inspectedCount);
            var page = await _processInstanceRepository.GetProcessStateSummaryPageForBlueprint(
                blueprintVersionId,
                nextCursor,
                pageSize,
                cancellationToken);

            if (page.Items.Count == 0)
            {
                break;
            }

            inspectedCount += page.Items.Count;

            var recentItemsOnPage = 0;
            foreach (var item in page.Items)
            {
                if (item.CreatedAt < rankingWindowStart)
                {
                    break;
                }

                recentItemsOnPage++;
            }

            recentProcessCount += recentItemsOnPage;

            if (recentItemsOnPage < page.Items.Count || string.IsNullOrWhiteSpace(page.ContinuationToken))
            {
                break;
            }

            nextCursor = page.ContinuationToken;
        }

        return recentProcessCount;
    }

    private List<ProcessHistoryPartitionState> CreatePartitionStates(
        BlueprintDefinition definition,
        string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return definition.Versions
                .Select(version => new ProcessHistoryPartitionState(version.Id))
                .ToList();
        }

        var decoded = DecodeCursor(cursor);
        var partitionsById = decoded.Partitions.ToDictionary(partition => partition.BlueprintVersionId);

        return definition.Versions
            .Select(version =>
            {
                if (!partitionsById.TryGetValue(version.Id, out var partition))
                {
                    return new ProcessHistoryPartitionState(version.Id);
                }

                return new ProcessHistoryPartitionState(
                    version.Id,
                    partition.ContinuationToken,
                    partition.IsExhausted,
                    partition.BufferedItems
                        .Select(item => new ProcessStateSummary
                        {
                            id = item.ProcessId,
                            BlueprintId = version.Id.ToString(),
                            CreatedAt = item.CreatedAt,
                            UpdatedAt = item.UpdatedAt,
                            Status = item.Status,
                        })
                        .ToList());
            })
            .ToList();
    }

    private async Task EnsurePartitionBuffersAsync(
        IReadOnlyList<ProcessHistoryPartitionState> partitionStates,
        int limit,
        CancellationToken cancellationToken)
    {
        foreach (var partitionState in partitionStates)
        {
            if (partitionState.Buffer.Count > 0 || partitionState.IsExhausted)
            {
                continue;
            }

            var page = await _processInstanceRepository.GetProcessStateSummaryPageForBlueprint(
                partitionState.BlueprintVersionId,
                partitionState.ContinuationToken,
                limit,
                cancellationToken);

            partitionState.ContinuationToken = page.ContinuationToken;
            partitionState.IsExhausted = string.IsNullOrWhiteSpace(page.ContinuationToken);

            if (page.Items.Count > 0)
            {
                partitionState.Buffer.AddRange(page.Items);
            }
        }
    }

    private static string EncodeCursor(IReadOnlyList<ProcessHistoryPartitionState> partitionStates)
    {
        var payload = new ProcessHistoryCursor(
            partitionStates.Select(partition => new ProcessHistoryCursorPartition(
                partition.BlueprintVersionId,
                partition.ContinuationToken,
                partition.IsExhausted,
                partition.Buffer.Select(item => new ProcessHistoryCursorItem(
                    item.ProcessId,
                    item.CreatedAt,
                    item.UpdatedAt,
                    item.Status)).ToList())).ToList());

        return Convert.ToBase64String(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, CursorSerializerOptions)));
    }

    private static ProcessHistoryCursor DecodeCursor(string cursor)
    {
        try
        {
            var payload = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var decoded = JsonSerializer.Deserialize<ProcessHistoryCursor>(payload, CursorSerializerOptions);
            if (decoded is null)
            {
                throw new InvalidOperationException("The process history cursor did not contain a payload.");
            }

            return decoded;
        }
        catch (Exception exception) when (
            exception is FormatException or JsonException or InvalidOperationException)
        {
            throw new ArgumentException("The process history cursor is invalid.", nameof(cursor), exception);
        }
    }

    private sealed record BlueprintVersionProcessActivity(
        DateTimeOffset? LatestProcessAt,
        int RecentProcessCount);

    private sealed class ProcessHistoryPartitionState
    {
        public ProcessHistoryPartitionState(
            int blueprintVersionId,
            string? continuationToken = null,
            bool isExhausted = false,
            List<ProcessStateSummary>? buffer = null)
        {
            BlueprintVersionId = blueprintVersionId;
            ContinuationToken = continuationToken;
            IsExhausted = isExhausted;
            Buffer = buffer ?? [];
        }

        public int BlueprintVersionId { get; }
        public string? ContinuationToken { get; set; }
        public bool IsExhausted { get; set; }
        public List<ProcessStateSummary> Buffer { get; }
    }

    private sealed record ProcessHistoryCursor(
        List<ProcessHistoryCursorPartition> Partitions);

    private sealed record ProcessHistoryCursorPartition(
        int BlueprintVersionId,
        string? ContinuationToken,
        bool IsExhausted,
        List<ProcessHistoryCursorItem> BufferedItems);

    private sealed record ProcessHistoryCursorItem(
        string ProcessId,
        DateTimeOffset CreatedAt,
        DateTimeOffset? UpdatedAt,
        ProcessStatus Status);
}
