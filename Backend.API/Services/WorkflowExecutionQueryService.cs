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
using Backend.Repositories.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Backend.API.Services;

public sealed class WorkflowExecutionQueryService
{
    private const int DefaultRankingWindowHours = 24;
    private const int ActivityInspectionPageSize = 25;
    private const int ActivityInspectionCapPerVersion = 100;
    private const string BlueprintExecutionActivityCacheKey = "workflow-execution-activity-index-v1";
    private static readonly JsonSerializerOptions CursorSerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly WorkflowExecutionProjectionService _projectionService;
    private readonly WorkflowDbContext _dbContext;
    private readonly IWorkflowInstanceRepository<Dictionary<string, JsonElement>> _workflowInstanceRepository;
    private readonly IMemoryCache _memoryCache;
    private readonly ObservabilityQueryService _observabilityQueryService;

    public WorkflowExecutionQueryService(
        WorkflowExecutionProjectionService projectionService,
        WorkflowDbContext dbContext,
        IWorkflowInstanceRepository<Dictionary<string, JsonElement>> workflowInstanceRepository,
        IMemoryCache memoryCache,
        ObservabilityQueryService observabilityQueryService)
    {
        _projectionService = projectionService;
        _dbContext = dbContext;
        _workflowInstanceRepository = workflowInstanceRepository;
        _memoryCache = memoryCache;
        _observabilityQueryService = observabilityQueryService;
    }

    public async Task<List<WorkflowExecutionSummaryViewModel>> GetExecutionsForBlueprintDefinitionAsync(
        int blueprintDefinitionId,
        CancellationToken cancellationToken) =>
        await _projectionService.GetExecutionsForBlueprintDefinitionAsync(
            blueprintDefinitionId,
            cancellationToken);

    public async Task<WorkflowExecutionDetailViewModel> GetExecutionDetailAsync(
        int blueprintDefinitionId,
        Guid workflowInstanceId,
        CancellationToken cancellationToken) =>
        await _projectionService.GetExecutionDetailAsync(
            blueprintDefinitionId,
            workflowInstanceId,
            cancellationToken);

    public async Task<WorkflowExecutionStepDetailViewModel> GetExecutionStepDetailAsync(
        int blueprintDefinitionId,
        Guid workflowInstanceId,
        Guid blueprintTaskInstanceId,
        CancellationToken cancellationToken) =>
        await _projectionService.GetExecutionStepDetailAsync(
            blueprintDefinitionId,
            workflowInstanceId,
            blueprintTaskInstanceId,
            cancellationToken);

    public async Task<NodeLogBatchViewModel> GetExecutionStepLogsAsync(
        int blueprintDefinitionId,
        Guid workflowInstanceId,
        Guid blueprintTaskInstanceId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var runtimeTaskInstanceId = await _projectionService.GetRuntimeTaskInstanceIdAsync(
            blueprintDefinitionId,
            workflowInstanceId,
            blueprintTaskInstanceId,
            cancellationToken);

        if (!runtimeTaskInstanceId.HasValue)
        {
            return new NodeLogBatchViewModel
            {
                Entries = [],
                NextCursor = cursor,
            };
        }

        return await _observabilityQueryService.GetNodeLogsAsync(
            workflowInstanceId,
            runtimeTaskInstanceId.Value,
            from,
            to,
            limit,
            cursor,
            cancellationToken);
    }

    public async Task<BlueprintExecutionActivityPageViewModel> GetExecutionBlueprintActivitiesAsync(
        string? query,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 50);

        var activities = await GetCachedExecutionActivityIndexAsync(cancellationToken);
        IEnumerable<BlueprintExecutionActivityViewModel> filtered = activities;

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

        return new BlueprintExecutionActivityPageViewModel
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            HasMore = page * pageSize < totalCount,
            RankingWindowHours = DefaultRankingWindowHours,
        };
    }

    public async Task<BlueprintExecutionHistoryPageViewModel> GetExecutionHistoryAsync(
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
            return new BlueprintExecutionHistoryPageViewModel
            {
                Items = [],
                HasMore = false,
                NextCursor = null,
            };
        }

        var partitionStates = CreatePartitionStates(definition, cursor);
        var versionsById = definition.Versions.ToDictionary(version => version.Id);
        var items = new List<BlueprintExecutionHistoryEntryViewModel>(limit);

        while (items.Count < limit)
        {
            await EnsurePartitionBuffersAsync(partitionStates, limit, cancellationToken);

            var nextPartition = partitionStates
                .Where(partition => partition.Buffer.Count > 0)
                .OrderByDescending(partition => partition.Buffer[0].CreatedAt)
                .ThenByDescending(partition => partition.Buffer[0].WorkflowInstanceId, StringComparer.Ordinal)
                .FirstOrDefault();

            if (nextPartition is null)
            {
                break;
            }

            var nextItem = nextPartition.Buffer[0];
            nextPartition.Buffer.RemoveAt(0);

            var version = versionsById[nextPartition.BlueprintVersionId];
            items.Add(new BlueprintExecutionHistoryEntryViewModel
            {
                WorkflowInstanceId = Guid.Parse(nextItem.WorkflowInstanceId),
                BlueprintDefinitionId = definition.Id,
                BlueprintVersionId = version.Id,
                BlueprintVersionNumber = version.Version,
                BlueprintVersionName = version.Name,
                Status = nextItem.Status.ToString(),
                CreatedAt = nextItem.CreatedAt,
                UpdatedAt = nextItem.UpdatedAt,
            });
        }

        var hasMore = partitionStates.Any(partition => partition.Buffer.Count > 0 || !partition.IsExhausted);

        return new BlueprintExecutionHistoryPageViewModel
        {
            Items = items,
            HasMore = hasMore,
            NextCursor = hasMore ? EncodeCursor(partitionStates) : null,
        };
    }

    private async Task<IReadOnlyList<BlueprintExecutionActivityViewModel>> GetCachedExecutionActivityIndexAsync(
        CancellationToken cancellationToken)
    {
        var cached = await _memoryCache.GetOrCreateAsync(
            BlueprintExecutionActivityCacheKey,
            async cacheEntry =>
            {
                cacheEntry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
                return await BuildExecutionActivityIndexAsync(cancellationToken);
            });

        return cached ?? [];
    }

    private async Task<IReadOnlyList<BlueprintExecutionActivityViewModel>> BuildExecutionActivityIndexAsync(
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
        var versionResults = new ConcurrentDictionary<int, BlueprintVersionExecutionActivity>();
        using var throttler = new SemaphoreSlim(8);

        var tasks = definitions
            .SelectMany(definition => definition.Versions)
            .Select(async version =>
            {
                await throttler.WaitAsync(cancellationToken);
                try
                {
                    var latestExecution = await _workflowInstanceRepository.GetLatestWorkflowStateForBlueprint(
                        version.Id,
                        cancellationToken);

                    if (latestExecution is null)
                    {
                        versionResults[version.Id] = new BlueprintVersionExecutionActivity(null, 0);
                        return;
                    }

                    var recentExecutionCount = await EstimateRecentExecutionCountAsync(
                        version.Id,
                        rankingWindowStart,
                        cancellationToken);

                    versionResults[version.Id] = new BlueprintVersionExecutionActivity(
                        latestExecution.CreatedAt,
                        recentExecutionCount);
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
                    .Cast<BlueprintVersionExecutionActivity>()
                    .ToList();

                var latestExecutionAt = results
                    .Where(result => result.LatestExecutionAt.HasValue)
                    .Select(result => result.LatestExecutionAt!.Value)
                    .DefaultIfEmpty()
                    .Max();

                if (latestExecutionAt == default)
                {
                    return null;
                }

                return new BlueprintExecutionActivityViewModel
                {
                    BlueprintDefinitionId = definition.Id,
                    BlueprintName = definition.DisplayName,
                    RecentExecutionCount = results.Sum(result => result.RecentExecutionCount),
                    LatestExecutionAt = latestExecutionAt,
                };
            })
            .Where(activity => activity is not null)
            .Cast<BlueprintExecutionActivityViewModel>()
            .OrderByDescending(activity => activity.RecentExecutionCount)
            .ThenByDescending(activity => activity.LatestExecutionAt)
            .ThenBy(activity => activity.BlueprintName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<int> EstimateRecentExecutionCountAsync(
        int blueprintVersionId,
        DateTimeOffset rankingWindowStart,
        CancellationToken cancellationToken)
    {
        var nextCursor = (string?)null;
        var recentExecutionCount = 0;
        var inspectedCount = 0;

        while (inspectedCount < ActivityInspectionCapPerVersion)
        {
            var pageSize = Math.Min(ActivityInspectionPageSize, ActivityInspectionCapPerVersion - inspectedCount);
            var page = await _workflowInstanceRepository.GetWorkflowStateSummaryPageForBlueprint(
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

            recentExecutionCount += recentItemsOnPage;

            if (recentItemsOnPage < page.Items.Count || string.IsNullOrWhiteSpace(page.ContinuationToken))
            {
                break;
            }

            nextCursor = page.ContinuationToken;
        }

        return recentExecutionCount;
    }

    private List<ExecutionHistoryPartitionState> CreatePartitionStates(
        BlueprintDefinition definition,
        string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return definition.Versions
                .Select(version => new ExecutionHistoryPartitionState(version.Id))
                .ToList();
        }

        var decoded = DecodeCursor(cursor);
        var partitionsById = decoded.Partitions.ToDictionary(partition => partition.BlueprintVersionId);

        return definition.Versions
            .Select(version =>
            {
                if (!partitionsById.TryGetValue(version.Id, out var partition))
                {
                    return new ExecutionHistoryPartitionState(version.Id);
                }

                return new ExecutionHistoryPartitionState(
                    version.Id,
                    partition.ContinuationToken,
                    partition.IsExhausted,
                    partition.BufferedItems
                        .Select(item => new WorkflowStateSummary
                        {
                            id = item.WorkflowInstanceId,
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
        IReadOnlyList<ExecutionHistoryPartitionState> partitionStates,
        int limit,
        CancellationToken cancellationToken)
    {
        foreach (var partitionState in partitionStates)
        {
            if (partitionState.Buffer.Count > 0 || partitionState.IsExhausted)
            {
                continue;
            }

            var page = await _workflowInstanceRepository.GetWorkflowStateSummaryPageForBlueprint(
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

    private static string EncodeCursor(IReadOnlyList<ExecutionHistoryPartitionState> partitionStates)
    {
        var payload = new ExecutionHistoryCursor(
            partitionStates.Select(partition => new ExecutionHistoryCursorPartition(
                partition.BlueprintVersionId,
                partition.ContinuationToken,
                partition.IsExhausted,
                partition.Buffer.Select(item => new ExecutionHistoryCursorItem(
                    item.WorkflowInstanceId,
                    item.CreatedAt,
                    item.UpdatedAt,
                    item.Status)).ToList())).ToList());

        return Convert.ToBase64String(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, CursorSerializerOptions)));
    }

    private static ExecutionHistoryCursor DecodeCursor(string cursor)
    {
        try
        {
            var payload = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var decoded = JsonSerializer.Deserialize<ExecutionHistoryCursor>(payload, CursorSerializerOptions);
            if (decoded is null)
            {
                throw new InvalidOperationException("The execution-history cursor did not contain a payload.");
            }

            return decoded;
        }
        catch (Exception exception) when (
            exception is FormatException or JsonException or InvalidOperationException)
        {
            throw new ArgumentException("The execution-history cursor is invalid.", nameof(cursor), exception);
        }
    }

    private sealed record BlueprintVersionExecutionActivity(
        DateTimeOffset? LatestExecutionAt,
        int RecentExecutionCount);

    private sealed class ExecutionHistoryPartitionState
    {
        public ExecutionHistoryPartitionState(
            int blueprintVersionId,
            string? continuationToken = null,
            bool isExhausted = false,
            List<WorkflowStateSummary>? buffer = null)
        {
            BlueprintVersionId = blueprintVersionId;
            ContinuationToken = continuationToken;
            IsExhausted = isExhausted;
            Buffer = buffer ?? [];
        }

        public int BlueprintVersionId { get; }
        public string? ContinuationToken { get; set; }
        public bool IsExhausted { get; set; }
        public List<WorkflowStateSummary> Buffer { get; }
    }

    private sealed record ExecutionHistoryCursor(
        List<ExecutionHistoryCursorPartition> Partitions);

    private sealed record ExecutionHistoryCursorPartition(
        int BlueprintVersionId,
        string? ContinuationToken,
        bool IsExhausted,
        List<ExecutionHistoryCursorItem> BufferedItems);

    private sealed record ExecutionHistoryCursorItem(
        string WorkflowInstanceId,
        DateTimeOffset CreatedAt,
        DateTimeOffset? UpdatedAt,
        WorkflowExecutionStatus Status);
}
