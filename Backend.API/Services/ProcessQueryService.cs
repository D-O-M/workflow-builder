using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Backend.API.Observability;
using Backend.Database.Relational;
using Backend.Database.Relational.Entities;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Models.InputModels;
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
    private const int IndexProjectionEmptyPageAdvanceLimit = 12;
    private const string BlueprintProcessActivityCacheKey = "process-activity-index-v1";
    private static readonly JsonSerializerOptions CursorSerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ProcessProjectionService _projectionService;
    private readonly BlueprintDbContext _dbContext;
    private readonly IProcessInstanceRepository<Dictionary<string, JsonElement>> _processInstanceRepository;
    private readonly IProcessStepRepository _processStepRepository;
    private readonly IProcessIndexProjectionService? _processIndexProjectionService;
    private readonly IMemoryCache _memoryCache;
    private readonly ObservabilityQueryService _observabilityQueryService;

    public ProcessQueryService(
        ProcessProjectionService projectionService,
        BlueprintDbContext dbContext,
        IProcessInstanceRepository<Dictionary<string, JsonElement>> processInstanceRepository,
        IProcessStepRepository processStepRepository,
        IMemoryCache memoryCache,
        ObservabilityQueryService observabilityQueryService,
        IProcessIndexProjectionService? processIndexProjectionService = null)
    {
        _projectionService = projectionService;
        _dbContext = dbContext;
        _processInstanceRepository = processInstanceRepository;
        _processStepRepository = processStepRepository;
        _processIndexProjectionService = processIndexProjectionService;
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

    public async Task<ProcessDetailViewModel> GetProcessDetailByIdAsync(
        Guid processId,
        CancellationToken cancellationToken) =>
        await _projectionService.GetProcessDetailByIdAsync(
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

        if (_processIndexProjectionService is not null && !IsLegacyProcessHistoryCursor(cursor))
        {
            var indexedPage = await GetProcessHistoryFromIndexAsync(
                definition,
                limit,
                cursor,
                cancellationToken);

            if (indexedPage.Items.Count > 0 ||
                !string.IsNullOrWhiteSpace(indexedPage.NextCursor) ||
                !string.IsNullOrWhiteSpace(cursor))
            {
                return indexedPage;
            }
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

    public async Task<ProcessSearchPageViewModel> SearchProcessesAsync(
        ProcessSearchRequestInputModel request,
        CancellationToken cancellationToken)
    {
        var normalizedRequest = NormalizeProcessSearchRequest(request);
        var filterSignature = BuildSearchFilterSignature(normalizedRequest);
        var definitions = await LoadSearchDefinitionsAsync(normalizedRequest, cancellationToken);
        var versionMetadata = BuildSearchVersionMetadata(definitions);

        if (versionMetadata.Count == 0)
        {
            return new ProcessSearchPageViewModel
            {
                Items = [],
                HasMore = false,
                NextCursor = null,
            };
        }

        var nodeFilters = BuildSearchNodeFilters(normalizedRequest, versionMetadata);
        var propertyFilters = BuildSearchPropertyFilters(normalizedRequest, versionMetadata);
        if (nodeFilters.Any(filter => filter.MatchingVersionIds.Count == 0)
            || propertyFilters.Any(filter => filter.MatchingVersionIds.Count == 0))
        {
            return new ProcessSearchPageViewModel
            {
                Items = [],
                HasMore = false,
                NextCursor = null,
            };
        }

        if (_processIndexProjectionService is not null && !IsLegacyProcessSearchCursor(normalizedRequest.Cursor))
        {
            var indexedResult = await SearchProcessesFromIndexAsync(
                normalizedRequest,
                filterSignature,
                versionMetadata,
                nodeFilters,
                propertyFilters,
                cancellationToken);

            if (indexedResult.SawProjectionRows ||
                indexedResult.Page.Items.Count > 0 ||
                !string.IsNullOrWhiteSpace(indexedResult.Page.NextCursor) ||
                !string.IsNullOrWhiteSpace(normalizedRequest.Cursor))
            {
                return indexedResult.Page;
            }
        }

        var partitions = CreateSearchPartitionStates(
            versionMetadata.Values.ToList(),
            normalizedRequest,
            filterSignature,
            normalizedRequest.Cursor);
        var items = new List<BlueprintProcessHistoryEntryViewModel>(normalizedRequest.Limit);
        var processStateCache = new Dictionary<string, ProcessInstanceRecord<Dictionary<string, JsonElement>>>(StringComparer.Ordinal);
        var processStepsCache = new Dictionary<string, IReadOnlyDictionary<string, ProcessStepRecord>>(StringComparer.Ordinal);

        while (items.Count < normalizedRequest.Limit)
        {
            await EnsureSearchPartitionBuffersAsync(partitions, normalizedRequest, cancellationToken);

            var nextPartition = partitions
                .Where(partition => partition.Buffer.Count > 0)
                .OrderByDescending(partition => partition.Buffer[0].CreatedAt)
                .ThenByDescending(partition => partition.Buffer[0].ProcessId, StringComparer.Ordinal)
                .FirstOrDefault();

            if (nextPartition is null)
            {
                break;
            }

            var nextSummary = nextPartition.Buffer[0];
            nextPartition.Buffer.RemoveAt(0);

            if (!versionMetadata.TryGetValue(nextPartition.BlueprintVersionId, out var metadata))
            {
                continue;
            }

            var candidateMatches = await CandidateMatchesSearchAsync(
                nextSummary,
                metadata,
                nodeFilters,
                propertyFilters,
                processStateCache,
                processStepsCache,
                cancellationToken);
            if (!candidateMatches)
            {
                continue;
            }

            items.Add(new BlueprintProcessHistoryEntryViewModel
            {
                ProcessId = Guid.Parse(nextSummary.ProcessId),
                BlueprintDefinitionId = metadata.BlueprintDefinitionId,
                BlueprintName = metadata.BlueprintName,
                BlueprintVersionId = metadata.BlueprintVersionId,
                BlueprintVersionNumber = metadata.BlueprintVersionNumber,
                Status = nextSummary.Status.ToString(),
                CreatedAt = nextSummary.CreatedAt,
                UpdatedAt = nextSummary.UpdatedAt,
            });
        }

        var hasMore = partitions.Any(partition => partition.Buffer.Count > 0 || !partition.IsExhausted);
        return new ProcessSearchPageViewModel
        {
            Items = items,
            HasMore = hasMore,
            NextCursor = hasMore ? EncodeSearchCursor(filterSignature, partitions) : null,
        };
    }

    private async Task<IndexedProcessSearchResult> SearchProcessesFromIndexAsync(
        NormalizedProcessSearchRequest normalizedRequest,
        string filterSignature,
        IReadOnlyDictionary<int, SearchVersionMetadata> versionMetadata,
        IReadOnlyList<SearchNodeFilterPlan> nodeFilters,
        IReadOnlyList<SearchPropertyFilterPlan> propertyFilters,
        CancellationToken cancellationToken)
    {
        var cursor = string.IsNullOrWhiteSpace(normalizedRequest.Cursor)
            ? null
            : DecodeIndexSearchCursor(normalizedRequest.Cursor, filterSignature);
        var continuationToken = cursor?.ProjectionContinuationToken;
        var items = new List<BlueprintProcessHistoryEntryViewModel>(normalizedRequest.Limit);
        var processStateCache = new Dictionary<string, ProcessInstanceRecord<Dictionary<string, JsonElement>>>(StringComparer.Ordinal);
        var processStepsCache = new Dictionary<string, IReadOnlyDictionary<string, ProcessStepRecord>>(StringComparer.Ordinal);
        var pageSize = Math.Clamp(normalizedRequest.Limit * 2, 10, 100);
        var sawProjectionRows = false;
        var emptyPageAdvances = 0;

        while (items.Count < normalizedRequest.Limit)
        {
            var page = await _processIndexProjectionService!.GetRecentTenantSummariesAsync(
                continuationToken,
                pageSize,
                normalizedRequest.CreatedFrom,
                normalizedRequest.CreatedTo,
                GetIndexSearchStatusFilter(normalizedRequest),
                cancellationToken);

            continuationToken = page.ContinuationToken;
            if (page.Items.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(continuationToken) ||
                    ++emptyPageAdvances >= IndexProjectionEmptyPageAdvanceLimit)
                {
                    break;
                }

                continue;
            }

            emptyPageAdvances = 0;
            sawProjectionRows = true;
            foreach (var summary in page.Items.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.ProcessId, StringComparer.Ordinal))
            {
                if (!versionMetadata.TryGetValue(summary.BlueprintVersionId, out var metadata) ||
                    !IndexSummaryMatchesSearchScope(summary, metadata, normalizedRequest))
                {
                    continue;
                }

                var processStateSummary = new ProcessStateSummary
                {
                    id = summary.ProcessId,
                    BlueprintId = summary.BlueprintVersionId.ToString(CultureInfo.InvariantCulture),
                    CreatedAt = summary.CreatedAt,
                    UpdatedAt = summary.UpdatedAt,
                    Status = summary.Status,
                };

                var candidateMatches = await CandidateMatchesSearchAsync(
                    processStateSummary,
                    metadata,
                    nodeFilters,
                    propertyFilters,
                    processStateCache,
                    processStepsCache,
                    cancellationToken);
                if (!candidateMatches)
                {
                    continue;
                }

                items.Add(new BlueprintProcessHistoryEntryViewModel
                {
                    ProcessId = Guid.Parse(summary.ProcessId),
                    BlueprintDefinitionId = metadata.BlueprintDefinitionId,
                    BlueprintName = metadata.BlueprintName,
                    BlueprintVersionId = metadata.BlueprintVersionId,
                    BlueprintVersionNumber = metadata.BlueprintVersionNumber,
                    Status = summary.Status.ToString(),
                    CreatedAt = summary.CreatedAt,
                    UpdatedAt = summary.UpdatedAt,
                });

                if (items.Count >= normalizedRequest.Limit)
                {
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(continuationToken))
            {
                break;
            }
        }

        return new IndexedProcessSearchResult(
            new ProcessSearchPageViewModel
            {
                Items = items,
                HasMore = !string.IsNullOrWhiteSpace(continuationToken),
                NextCursor = string.IsNullOrWhiteSpace(continuationToken)
                    ? null
                    : EncodeIndexSearchCursor(filterSignature, continuationToken),
            },
            sawProjectionRows);
    }

    private async Task<BlueprintProcessHistoryPageViewModel> GetProcessHistoryFromIndexAsync(
        BlueprintDefinition definition,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var versionsById = definition.Versions.ToDictionary(version => version.Id);
        var continuationToken = cursor;
        var items = new List<BlueprintProcessHistoryEntryViewModel>(limit);
        var emptyPageAdvances = 0;

        while (items.Count < limit)
        {
            var page = await _processIndexProjectionService!.GetRecentBlueprintSummariesAsync(
                definition.Id,
                versionsById.Keys.ToArray(),
                continuationToken,
                limit,
                createdFrom: null,
                createdTo: null,
                statuses: null,
                cancellationToken);

            continuationToken = page.ContinuationToken;
            if (page.Items.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(continuationToken) ||
                    ++emptyPageAdvances >= IndexProjectionEmptyPageAdvanceLimit)
                {
                    break;
                }

                continue;
            }

            emptyPageAdvances = 0;
            foreach (var item in page.Items
                .Where(item => versionsById.ContainsKey(item.BlueprintVersionId))
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.ProcessId, StringComparer.Ordinal))
            {
                var version = versionsById[item.BlueprintVersionId];
                items.Add(new BlueprintProcessHistoryEntryViewModel
                {
                    ProcessId = Guid.Parse(item.ProcessId),
                    BlueprintDefinitionId = definition.Id,
                    BlueprintName = definition.DisplayName,
                    BlueprintVersionId = version.Id,
                    BlueprintVersionNumber = version.Version,
                    Status = item.Status.ToString(),
                    CreatedAt = item.CreatedAt,
                    UpdatedAt = item.UpdatedAt,
                });

                if (items.Count >= limit)
                {
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(continuationToken))
            {
                break;
            }
        }

        return new BlueprintProcessHistoryPageViewModel
        {
            Items = items,
            HasMore = !string.IsNullOrWhiteSpace(continuationToken),
            NextCursor = continuationToken,
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
        if (_processIndexProjectionService is not null)
        {
            var indexedActivities = await BuildProcessActivityIndexFromIndexAsync(
                definitions,
                rankingWindowStart,
                cancellationToken);

            if (indexedActivities.Count > 0)
            {
                return indexedActivities;
            }
        }

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

    private async Task<IReadOnlyList<BlueprintProcessActivityViewModel>> BuildProcessActivityIndexFromIndexAsync(
        IReadOnlyList<BlueprintDefinition> definitions,
        DateTimeOffset rankingWindowStart,
        CancellationToken cancellationToken)
    {
        var activities = new List<BlueprintProcessActivityViewModel>();
        foreach (var definition in definitions)
        {
            if (definition.Versions.Count == 0)
            {
                continue;
            }

            var page = await _processIndexProjectionService!.GetRecentBlueprintSummariesAsync(
                definition.Id,
                definition.Versions.Select(version => version.Id).ToArray(),
                continuationToken: null,
                maxItemCount: ActivityInspectionCapPerVersion,
                createdFrom: rankingWindowStart,
                createdTo: null,
                statuses: null,
                cancellationToken);

            if (page.Items.Count == 0)
            {
                continue;
            }

            activities.Add(new BlueprintProcessActivityViewModel
            {
                BlueprintDefinitionId = definition.Id,
                BlueprintName = definition.DisplayName,
                RecentProcessCount = page.Items.Count,
                LatestProcessAt = page.Items.Max(item => item.CreatedAt),
            });
        }

        return activities
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

    private async Task<List<BlueprintDefinition>> LoadSearchDefinitionsAsync(
        NormalizedProcessSearchRequest request,
        CancellationToken cancellationToken)
    {
        var query = _dbContext.BlueprintDefinitions
            .Include(definition => definition.Versions)
                .ThenInclude(version => version.BlueprintNodes)
            .AsNoTracking();

        if (request.BlueprintDefinitionIds.Count > 0)
        {
            query = query.Where(definition => request.BlueprintDefinitionIds.Contains(definition.Id));
        }

        return await query.ToListAsync(cancellationToken);
    }

    private static Dictionary<int, SearchVersionMetadata> BuildSearchVersionMetadata(
        IEnumerable<BlueprintDefinition> definitions) =>
        definitions
            .SelectMany(definition => definition.Versions.Select(version => new SearchVersionMetadata(
                definition.Id,
                definition.DisplayName,
                version.Id,
                version.Version,
                version.BlueprintNodes.Select(node => node.Id).ToHashSet())))
            .ToDictionary(metadata => metadata.BlueprintVersionId);

    private static List<SearchNodeFilterPlan> BuildSearchNodeFilters(
        NormalizedProcessSearchRequest request,
        IReadOnlyDictionary<int, SearchVersionMetadata> versionMetadata) =>
        request.NodeFilters
            .Select(filter => new SearchNodeFilterPlan(
                filter.BlueprintDefinitionId,
                filter.BlueprintNodeId,
                filter.Statuses,
                versionMetadata.Values
                    .Where(metadata =>
                        metadata.BlueprintDefinitionId == filter.BlueprintDefinitionId
                        && metadata.BlueprintNodeIds.Contains(filter.BlueprintNodeId))
                    .Select(metadata => metadata.BlueprintVersionId)
                    .ToHashSet()))
            .ToList();

    private static List<SearchPropertyFilterPlan> BuildSearchPropertyFilters(
        NormalizedProcessSearchRequest request,
        IReadOnlyDictionary<int, SearchVersionMetadata> versionMetadata) =>
        request.PropertyFilters
            .Select(filter => new SearchPropertyFilterPlan(
                filter.Scope,
                filter.BlueprintDefinitionId,
                filter.BlueprintNodeId,
                filter.PropertyKey,
                filter.PropertyType,
                filter.StringValue,
                filter.IntegerValue,
                filter.DecimalValue,
                filter.BooleanValue,
                filter.GuidValue,
                filter.DateValue,
                filter.DateTimeValue,
                ResolveMatchingVersionIds(filter, versionMetadata)))
            .ToList();

    private static HashSet<int> ResolveMatchingVersionIds(
        NormalizedProcessSearchPropertyFilter filter,
        IReadOnlyDictionary<int, SearchVersionMetadata> versionMetadata)
    {
        IEnumerable<SearchVersionMetadata> candidates = versionMetadata.Values;

        if (filter.BlueprintDefinitionId.HasValue)
        {
            candidates = candidates.Where(metadata => metadata.BlueprintDefinitionId == filter.BlueprintDefinitionId.Value);
        }

        if (filter.Scope is SearchPropertyScope.NodeInput or SearchPropertyScope.NodeOutput)
        {
            if (!filter.BlueprintNodeId.HasValue)
            {
                return [];
            }

            candidates = candidates.Where(metadata => metadata.BlueprintNodeIds.Contains(filter.BlueprintNodeId.Value));
        }

        return candidates.Select(metadata => metadata.BlueprintVersionId).ToHashSet();
    }

    private async Task EnsureSearchPartitionBuffersAsync(
        IReadOnlyList<SearchPartitionState> partitions,
        NormalizedProcessSearchRequest request,
        CancellationToken cancellationToken)
    {
        var pageSize = Math.Clamp(request.Limit, 10, 50);
        foreach (var partition in partitions)
        {
            if (partition.Buffer.Count > 0 || partition.IsExhausted)
            {
                continue;
            }

            var page = await _processInstanceRepository.GetProcessStateSummaryPageForBlueprint(
                partition.BlueprintVersionId,
                partition.ContinuationToken,
                pageSize,
                request.CreatedFrom,
                request.CreatedTo,
                partition.ProcessStatuses.Count == 0 ? null : partition.ProcessStatuses,
                cancellationToken);

            partition.ContinuationToken = page.ContinuationToken;
            partition.IsExhausted = string.IsNullOrWhiteSpace(page.ContinuationToken);
            if (page.Items.Count > 0)
            {
                partition.Buffer.AddRange(page.Items);
            }
        }
    }

    private async Task<bool> CandidateMatchesSearchAsync(
        ProcessStateSummary summary,
        SearchVersionMetadata metadata,
        IReadOnlyList<SearchNodeFilterPlan> nodeFilters,
        IReadOnlyList<SearchPropertyFilterPlan> propertyFilters,
        IDictionary<string, ProcessInstanceRecord<Dictionary<string, JsonElement>>> processStateCache,
        IDictionary<string, IReadOnlyDictionary<string, ProcessStepRecord>> processStepsCache,
        CancellationToken cancellationToken)
    {
        if (nodeFilters.Any(filter => !filter.MatchingVersionIds.Contains(metadata.BlueprintVersionId))
            || propertyFilters.Any(filter => !filter.MatchingVersionIds.Contains(metadata.BlueprintVersionId)))
        {
            return false;
        }

        IReadOnlyDictionary<string, ProcessStepRecord>? processSteps = null;
        ProcessInstanceRecord<Dictionary<string, JsonElement>>? processState = null;
        var processId = Guid.Parse(summary.ProcessId);
        var processCacheKey = $"{metadata.BlueprintVersionId}:{summary.ProcessId}";

        if (propertyFilters.Any(filter => filter.Scope == SearchPropertyScope.GlobalState))
        {
            processState = await GetProcessStateAsync(
                processCacheKey,
                processId,
                metadata.BlueprintVersionId,
                processStateCache,
                cancellationToken);

            foreach (var filter in propertyFilters.Where(filter => filter.Scope == SearchPropertyScope.GlobalState))
            {
                if (!processState.GlobalState.TryGetValue(filter.PropertyKey, out var propertyValue)
                    || !MatchesPropertyFilter(propertyValue, filter))
                {
                    return false;
                }
            }
        }

        if (nodeFilters.Count > 0 || propertyFilters.Any(filter => filter.Scope != SearchPropertyScope.GlobalState))
        {
            processSteps = await GetProcessStepsAsync(
                summary.ProcessId,
                processId,
                processStepsCache,
                cancellationToken);
        }

        if (processSteps is not null)
        {
            foreach (var filter in nodeFilters)
            {
                if (!processSteps.TryGetValue(filter.BlueprintNodeId.ToString(), out var step)
                    || (filter.Statuses.Count > 0 && !filter.Statuses.Contains(step.Status)))
                {
                    return false;
                }
            }

            foreach (var filter in propertyFilters.Where(filter => filter.Scope != SearchPropertyScope.GlobalState))
            {
                if (!filter.BlueprintNodeId.HasValue
                    || !processSteps.TryGetValue(filter.BlueprintNodeId.Value.ToString(), out var step)
                    || !TryGetNodePropertyValue(step, filter.Scope, filter.PropertyKey, out var propertyValue)
                    || !MatchesPropertyFilter(propertyValue, filter))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private async Task<ProcessInstanceRecord<Dictionary<string, JsonElement>>> GetProcessStateAsync(
        string cacheKey,
        Guid processId,
        int blueprintVersionId,
        IDictionary<string, ProcessInstanceRecord<Dictionary<string, JsonElement>>> cache,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(cacheKey, out var processState))
        {
            return processState;
        }

        processState = await _processInstanceRepository.GetProcessStateById(processId, blueprintVersionId, cancellationToken);
        cache[cacheKey] = processState;
        return processState;
    }

    private async Task<IReadOnlyDictionary<string, ProcessStepRecord>> GetProcessStepsAsync(
        string cacheKey,
        Guid processId,
        IDictionary<string, IReadOnlyDictionary<string, ProcessStepRecord>> cache,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(cacheKey, out var processSteps))
        {
            return processSteps;
        }

        processSteps = (await _processStepRepository.GetProcessStepsForProcess(processId, cancellationToken))
            .GroupBy(step => step.BlueprintNodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(step => step.CreatedAt).First(), StringComparer.Ordinal);
        cache[cacheKey] = processSteps;
        return processSteps;
    }

    private static bool TryGetNodePropertyValue(
        ProcessStepRecord step,
        SearchPropertyScope scope,
        string propertyKey,
        out JsonElement propertyValue)
    {
        var channels = scope == SearchPropertyScope.NodeInput
            ? step.InputData
            : step.OutputData;

        if (channels is not null)
        {
            foreach (var channelPayload in channels.Values)
            {
                if (channelPayload.ValueKind == JsonValueKind.Object
                    && channelPayload.TryGetProperty(propertyKey, out propertyValue))
                {
                    propertyValue = propertyValue.Clone();
                    return true;
                }
            }
        }

        propertyValue = default;
        return false;
    }

    private static bool MatchesPropertyFilter(JsonElement propertyValue, SearchPropertyFilterPlan filter) =>
        filter.PropertyType switch
        {
            SearchPropertyType.String => TryGetString(propertyValue, out var stringValue)
                && string.Equals(stringValue, filter.StringValue, StringComparison.Ordinal),
            SearchPropertyType.Enum => TryGetString(propertyValue, out var enumValue)
                && string.Equals(enumValue, filter.StringValue, StringComparison.Ordinal),
            SearchPropertyType.Guid => TryGetGuid(propertyValue, out var guidValue)
                && filter.GuidValue.HasValue
                && guidValue == filter.GuidValue.Value,
            SearchPropertyType.Boolean => TryGetBoolean(propertyValue, out var booleanValue)
                && filter.BooleanValue.HasValue
                && booleanValue == filter.BooleanValue.Value,
            SearchPropertyType.Integer => TryGetInteger(propertyValue, out var integerValue)
                && filter.IntegerValue.HasValue
                && integerValue == filter.IntegerValue.Value,
            SearchPropertyType.Decimal => TryGetDecimal(propertyValue, out var decimalValue)
                && filter.DecimalValue.HasValue
                && decimalValue == filter.DecimalValue.Value,
            SearchPropertyType.Date => TryGetDate(propertyValue, out var dateValue)
                && filter.DateValue.HasValue
                && dateValue == filter.DateValue.Value,
            SearchPropertyType.DateTime => TryGetDateTime(propertyValue, out var dateTimeValue)
                && filter.DateTimeValue.HasValue
                && dateTimeValue.ToUniversalTime() == filter.DateTimeValue.Value.ToUniversalTime(),
            _ => false,
        };

    private static bool TryGetString(JsonElement value, out string stringValue)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            stringValue = value.GetString() ?? string.Empty;
            return true;
        }

        stringValue = string.Empty;
        return false;
    }

    private static bool TryGetGuid(JsonElement value, out Guid guidValue)
    {
        if (value.ValueKind == JsonValueKind.String
            && Guid.TryParse(value.GetString(), out guidValue))
        {
            return true;
        }

        guidValue = Guid.Empty;
        return false;
    }

    private static bool TryGetBoolean(JsonElement value, out bool booleanValue)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.True:
            case JsonValueKind.False:
                booleanValue = value.GetBoolean();
                return true;
            case JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed):
                booleanValue = parsed;
                return true;
            default:
                booleanValue = false;
                return false;
        }
    }

    private static bool TryGetInteger(JsonElement value, out decimal integerValue)
    {
        if (TryGetDecimal(value, out var parsedDecimal)
            && parsedDecimal == decimal.Truncate(parsedDecimal))
        {
            integerValue = parsedDecimal;
            return true;
        }

        integerValue = 0;
        return false;
    }

    private static bool TryGetDecimal(JsonElement value, out decimal numberValue)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number when value.TryGetDecimal(out var parsedDecimal):
                numberValue = parsedDecimal;
                return true;
            case JsonValueKind.Number when value.TryGetDouble(out var parsedDouble):
                numberValue = Convert.ToDecimal(parsedDouble, CultureInfo.InvariantCulture);
                return true;
            case JsonValueKind.String when decimal.TryParse(
                value.GetString(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var parsedFromString):
                numberValue = parsedFromString;
                return true;
            default:
                numberValue = 0;
                return false;
        }
    }

    private static bool TryGetDate(JsonElement value, out DateOnly dateValue)
    {
        if (value.ValueKind == JsonValueKind.String
            && TryParseDate(value.GetString(), out dateValue))
        {
            return true;
        }

        dateValue = default;
        return false;
    }

    private static bool TryGetDateTime(JsonElement value, out DateTimeOffset dateTimeValue)
    {
        if (value.ValueKind == JsonValueKind.String
            && TryParseDateTime(value.GetString(), out dateTimeValue))
        {
            return true;
        }

        dateTimeValue = default;
        return false;
    }

    private static bool TryParseDate(string? value, out DateOnly dateValue)
    {
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dateValue))
        {
            return true;
        }

        return DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out dateValue);
    }

    private static bool TryParseDateTime(string? value, out DateTimeOffset dateTimeValue) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind,
            out dateTimeValue);

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

    private static bool IsLegacyProcessHistoryCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        try
        {
            _ = DecodeCursor(cursor);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string EncodeSearchCursor(string filterSignature, IReadOnlyList<SearchPartitionState> partitionStates)
    {
        var payload = new ProcessSearchCursor(
            filterSignature,
            partitionStates.Select(partition => new ProcessSearchCursorPartition(
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

    private static List<SearchPartitionState> CreateSearchPartitionStates(
        IReadOnlyList<SearchVersionMetadata> metadata,
        NormalizedProcessSearchRequest request,
        string filterSignature,
        string? cursor)
    {
        var statusesByBlueprintDefinitionId = request.BlueprintFilters
            .ToDictionary(filter => filter.BlueprintDefinitionId, filter => filter.Statuses);

        HashSet<ProcessStatus> ResolveStatuses(SearchVersionMetadata version) =>
            statusesByBlueprintDefinitionId.TryGetValue(version.BlueprintDefinitionId, out var statuses)
                ? statuses
                : request.ProcessStatuses;

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return metadata
                .Select(version => new SearchPartitionState(
                    version.BlueprintVersionId,
                    ResolveStatuses(version)))
                .ToList();
        }

        var decoded = DecodeSearchCursor(cursor);
        if (!string.Equals(decoded.FilterSignature, filterSignature, StringComparison.Ordinal))
        {
            throw new ArgumentException("The search cursor does not match the active filter set.", nameof(cursor));
        }

        var partitionsById = decoded.Partitions.ToDictionary(partition => partition.BlueprintVersionId);
        return metadata
            .Select(version =>
            {
                if (!partitionsById.TryGetValue(version.BlueprintVersionId, out var partition))
                {
                    return new SearchPartitionState(
                        version.BlueprintVersionId,
                        ResolveStatuses(version));
                }

                return new SearchPartitionState(
                    version.BlueprintVersionId,
                    ResolveStatuses(version),
                    partition.ContinuationToken,
                    partition.IsExhausted,
                    partition.BufferedItems.Select(item => new ProcessStateSummary
                    {
                        id = item.ProcessId,
                        BlueprintId = version.BlueprintVersionId.ToString(),
                        CreatedAt = item.CreatedAt,
                        UpdatedAt = item.UpdatedAt,
                        Status = item.Status,
                    }).ToList());
            })
            .ToList();
    }

    private static ProcessSearchCursor DecodeSearchCursor(string cursor)
    {
        try
        {
            var payload = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var decoded = JsonSerializer.Deserialize<ProcessSearchCursor>(payload, CursorSerializerOptions);
            if (decoded is null)
            {
                throw new InvalidOperationException("The process search cursor did not contain a payload.");
            }

            return decoded;
        }
        catch (Exception exception) when (
            exception is FormatException or JsonException or InvalidOperationException)
        {
            throw new ArgumentException("The process search cursor is invalid.", nameof(cursor), exception);
        }
    }

    private static bool IsLegacyProcessSearchCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        try
        {
            var decoded = DecodeSearchCursor(cursor);
            return decoded.Partitions is not null;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static IReadOnlyCollection<ProcessStatus>? GetIndexSearchStatusFilter(
        NormalizedProcessSearchRequest request)
    {
        if (request.BlueprintFilters.Count > 0)
        {
            return null;
        }

        return request.ProcessStatuses.Count > 0
            ? request.ProcessStatuses
            : null;
    }

    private static bool IndexSummaryMatchesSearchScope(
        ProcessSummaryProjectionRecord summary,
        SearchVersionMetadata metadata,
        NormalizedProcessSearchRequest request)
    {
        if (request.BlueprintDefinitionIds.Count > 0 &&
            !request.BlueprintDefinitionIds.Contains(metadata.BlueprintDefinitionId))
        {
            return false;
        }

        var blueprintFilter = request.BlueprintFilters
            .FirstOrDefault(filter => filter.BlueprintDefinitionId == metadata.BlueprintDefinitionId);

        if (blueprintFilter is not null)
        {
            return blueprintFilter.Statuses.Count == 0 ||
                blueprintFilter.Statuses.Contains(summary.Status);
        }

        return request.ProcessStatuses.Count == 0 ||
            request.ProcessStatuses.Contains(summary.Status);
    }

    private static string EncodeIndexSearchCursor(string filterSignature, string projectionContinuationToken)
    {
        var payload = new ProcessIndexSearchCursor(filterSignature, projectionContinuationToken);
        return Convert.ToBase64String(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, CursorSerializerOptions)));
    }

    private static ProcessIndexSearchCursor DecodeIndexSearchCursor(string cursor, string filterSignature)
    {
        try
        {
            var payload = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var decoded = JsonSerializer.Deserialize<ProcessIndexSearchCursor>(payload, CursorSerializerOptions);
            if (decoded is null)
            {
                throw new InvalidOperationException("The process search cursor did not contain a payload.");
            }

            if (!string.Equals(decoded.FilterSignature, filterSignature, StringComparison.Ordinal))
            {
                throw new ArgumentException("The search cursor does not match the active filter set.", nameof(cursor));
            }

            return decoded;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is FormatException or JsonException or InvalidOperationException)
        {
            throw new ArgumentException("The process search cursor is invalid.", nameof(cursor), exception);
        }
    }

    private static string BuildSearchFilterSignature(NormalizedProcessSearchRequest request)
    {
        var payload = new
        {
            request.BlueprintDefinitionIds,
            BlueprintFilters = request.BlueprintFilters
                .Select(filter => new
                {
                    filter.BlueprintDefinitionId,
                    Statuses = filter.Statuses.Select(status => status.ToString()).OrderBy(status => status).ToList(),
                })
                .OrderBy(filter => filter.BlueprintDefinitionId)
                .ToList(),
            ProcessStatuses = request.ProcessStatuses.Select(status => status.ToString()).OrderBy(status => status).ToList(),
            request.CreatedFrom,
            request.CreatedTo,
            NodeFilters = request.NodeFilters
                .Select(filter => new
                {
                    filter.BlueprintDefinitionId,
                    filter.BlueprintNodeId,
                    Statuses = filter.Statuses.Select(status => status.ToString()).OrderBy(status => status).ToList(),
                })
                .OrderBy(filter => filter.BlueprintDefinitionId)
                .ThenBy(filter => filter.BlueprintNodeId)
                .ToList(),
            PropertyFilters = request.PropertyFilters
                .Select(filter => new
                {
                    Scope = filter.Scope.ToString(),
                    filter.BlueprintDefinitionId,
                    filter.BlueprintNodeId,
                    filter.PropertyKey,
                    PropertyType = filter.PropertyType.ToString(),
                    filter.StringValue,
                    filter.IntegerValue,
                    filter.DecimalValue,
                    filter.BooleanValue,
                    filter.GuidValue,
                    filter.DateValue,
                    DateTimeValue = filter.DateTimeValue?.ToString("O", CultureInfo.InvariantCulture),
                })
                .OrderBy(filter => filter.Scope)
                .ThenBy(filter => filter.BlueprintDefinitionId)
                .ThenBy(filter => filter.BlueprintNodeId)
                .ThenBy(filter => filter.PropertyKey, StringComparer.Ordinal)
                .ToList(),
        };

        var json = JsonSerializer.Serialize(payload, CursorSerializerOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static NormalizedProcessSearchRequest NormalizeProcessSearchRequest(ProcessSearchRequestInputModel request)
    {
        var createdFrom = request.CreatedFrom;
        var createdTo = request.CreatedTo;
        if (createdFrom == default || createdTo == default)
        {
            throw new ArgumentException("Search requests must include a created-from and created-to window.", nameof(request));
        }

        if (createdTo < createdFrom)
        {
            throw new ArgumentException("Search created-to must be on or after created-from.", nameof(request));
        }

        var processStatuses = request.ProcessStatuses
            .Where(status => !string.IsNullOrWhiteSpace(status))
            .Select(ParseProcessStatus)
            .Distinct()
            .ToHashSet();

        var blueprintFilters = request.BlueprintFilters
            .Where(filter => filter.BlueprintDefinitionId > 0)
            .GroupBy(filter => filter.BlueprintDefinitionId)
            .Select(group => new NormalizedProcessSearchBlueprintFilter(
                group.Key,
                group.SelectMany(filter => filter.Statuses)
                    .Where(status => !string.IsNullOrWhiteSpace(status))
                    .Select(ParseProcessStatus)
                    .Distinct()
                    .ToHashSet()))
            .OrderBy(filter => filter.BlueprintDefinitionId)
            .ToList();

        var blueprintDefinitionIds = request.BlueprintDefinitionIds
            .Where(id => id > 0)
            .Concat(blueprintFilters.Select(filter => filter.BlueprintDefinitionId))
            .Distinct()
            .OrderBy(id => id)
            .ToList();

        var nodeFilters = request.NodeFilters
            .Select(filter => new NormalizedProcessSearchNodeFilter(
                filter.BlueprintDefinitionId,
                filter.BlueprintNodeId,
                filter.Statuses
                    .Where(status => !string.IsNullOrWhiteSpace(status))
                    .Select(ParseProcessStepStatus)
                    .Distinct()
                    .ToHashSet()))
            .ToList();

        var propertyFilters = request.PropertyFilters
            .Select(NormalizePropertyFilter)
            .ToList();

        return new NormalizedProcessSearchRequest(
            blueprintDefinitionIds,
            blueprintFilters,
            processStatuses,
            createdFrom.ToUniversalTime(),
            createdTo.ToUniversalTime(),
            nodeFilters,
            propertyFilters,
            Math.Clamp(request.Limit, 1, 50),
            request.Cursor);
    }

    private static NormalizedProcessSearchPropertyFilter NormalizePropertyFilter(
        ProcessSearchPropertyFilterInputModel filter)
    {
        var propertyKey = filter.PropertyKey.Trim();
        if (string.IsNullOrWhiteSpace(propertyKey))
        {
            throw new ArgumentException("Property filters must include a property key.", nameof(filter));
        }

        var propertyType = ParsePropertyType(filter.PropertyType);
        var scope = ParsePropertyScope(filter.Scope);
        var rawValue = filter.Value.Trim();
        if (rawValue.Length == 0)
        {
            throw new ArgumentException("Property filters must include a value.", nameof(filter));
        }

        decimal? integerValue = null;
        decimal? decimalValue = null;
        bool? booleanValue = null;
        Guid? guidValue = null;
        DateOnly? dateValue = null;
        DateTimeOffset? dateTimeValue = null;

        switch (propertyType)
        {
            case SearchPropertyType.Integer:
                if (!decimal.TryParse(rawValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedInteger)
                    || parsedInteger != decimal.Truncate(parsedInteger))
                {
                    throw new ArgumentException($"'{rawValue}' is not a valid integer property value.", nameof(filter));
                }

                integerValue = parsedInteger;
                rawValue = parsedInteger.ToString(CultureInfo.InvariantCulture);
                break;
            case SearchPropertyType.Decimal:
                if (!decimal.TryParse(rawValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedDecimal))
                {
                    throw new ArgumentException($"'{rawValue}' is not a valid decimal property value.", nameof(filter));
                }

                decimalValue = parsedDecimal;
                rawValue = parsedDecimal.ToString(CultureInfo.InvariantCulture);
                break;
            case SearchPropertyType.Boolean:
                if (!bool.TryParse(rawValue, out var parsedBoolean))
                {
                    throw new ArgumentException($"'{rawValue}' is not a valid boolean property value.", nameof(filter));
                }

                booleanValue = parsedBoolean;
                rawValue = parsedBoolean.ToString();
                break;
            case SearchPropertyType.Guid:
                if (!Guid.TryParse(rawValue, out var parsedGuid))
                {
                    throw new ArgumentException($"'{rawValue}' is not a valid GUID property value.", nameof(filter));
                }

                guidValue = parsedGuid;
                rawValue = parsedGuid.ToString("D");
                break;
            case SearchPropertyType.Date:
                if (!TryParseDate(rawValue, out var parsedDate))
                {
                    throw new ArgumentException($"'{rawValue}' is not a valid date property value.", nameof(filter));
                }

                dateValue = parsedDate;
                rawValue = parsedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                break;
            case SearchPropertyType.DateTime:
                if (!TryParseDateTime(rawValue, out var parsedDateTime))
                {
                    throw new ArgumentException($"'{rawValue}' is not a valid date-time property value.", nameof(filter));
                }

                dateTimeValue = parsedDateTime.ToUniversalTime();
                rawValue = dateTimeValue.Value.ToString("O", CultureInfo.InvariantCulture);
                break;
        }

        if (scope is SearchPropertyScope.NodeInput or SearchPropertyScope.NodeOutput)
        {
            if (!filter.BlueprintDefinitionId.HasValue || !filter.BlueprintNodeId.HasValue)
            {
                throw new ArgumentException("Node property filters must include a blueprint and node reference.", nameof(filter));
            }
        }

        return new NormalizedProcessSearchPropertyFilter(
            scope,
            filter.BlueprintDefinitionId,
            filter.BlueprintNodeId,
            propertyKey,
            propertyType,
            rawValue,
            integerValue,
            decimalValue,
            booleanValue,
            guidValue,
            dateValue,
            dateTimeValue);
    }

    private static ProcessStatus ParseProcessStatus(string status) =>
        Enum.TryParse<ProcessStatus>(status, ignoreCase: true, out var parsedStatus)
            ? parsedStatus
            : throw new ArgumentException($"'{status}' is not a supported process status.", nameof(status));

    private static ProcessStepStatus ParseProcessStepStatus(string status) =>
        Enum.TryParse<ProcessStepStatus>(status, ignoreCase: true, out var parsedStatus)
            ? parsedStatus
            : throw new ArgumentException($"'{status}' is not a supported step status.", nameof(status));

    private static SearchPropertyScope ParsePropertyScope(string scope) =>
        Enum.TryParse<SearchPropertyScope>(scope, ignoreCase: true, out var parsedScope)
            ? parsedScope
            : throw new ArgumentException($"'{scope}' is not a supported property scope.", nameof(scope));

    private static SearchPropertyType ParsePropertyType(string propertyType) =>
        Enum.TryParse<SearchPropertyType>(propertyType, ignoreCase: true, out var parsedType)
            ? parsedType
            : throw new ArgumentException($"'{propertyType}' is not a supported property type.", nameof(propertyType));

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

    private sealed record NormalizedProcessSearchRequest(
        List<int> BlueprintDefinitionIds,
        List<NormalizedProcessSearchBlueprintFilter> BlueprintFilters,
        HashSet<ProcessStatus> ProcessStatuses,
        DateTimeOffset CreatedFrom,
        DateTimeOffset CreatedTo,
        List<NormalizedProcessSearchNodeFilter> NodeFilters,
        List<NormalizedProcessSearchPropertyFilter> PropertyFilters,
        int Limit,
        string? Cursor);

    private sealed record NormalizedProcessSearchBlueprintFilter(
        int BlueprintDefinitionId,
        HashSet<ProcessStatus> Statuses);

    private sealed record NormalizedProcessSearchNodeFilter(
        int BlueprintDefinitionId,
        Guid BlueprintNodeId,
        HashSet<ProcessStepStatus> Statuses);

    private sealed record NormalizedProcessSearchPropertyFilter(
        SearchPropertyScope Scope,
        int? BlueprintDefinitionId,
        Guid? BlueprintNodeId,
        string PropertyKey,
        SearchPropertyType PropertyType,
        string StringValue,
        decimal? IntegerValue,
        decimal? DecimalValue,
        bool? BooleanValue,
        Guid? GuidValue,
        DateOnly? DateValue,
        DateTimeOffset? DateTimeValue);

    private sealed record SearchVersionMetadata(
        int BlueprintDefinitionId,
        string BlueprintName,
        int BlueprintVersionId,
        int BlueprintVersionNumber,
        HashSet<Guid> BlueprintNodeIds);

    private sealed record SearchNodeFilterPlan(
        int BlueprintDefinitionId,
        Guid BlueprintNodeId,
        HashSet<ProcessStepStatus> Statuses,
        HashSet<int> MatchingVersionIds);

    private sealed record SearchPropertyFilterPlan(
        SearchPropertyScope Scope,
        int? BlueprintDefinitionId,
        Guid? BlueprintNodeId,
        string PropertyKey,
        SearchPropertyType PropertyType,
        string StringValue,
        decimal? IntegerValue,
        decimal? DecimalValue,
        bool? BooleanValue,
        Guid? GuidValue,
        DateOnly? DateValue,
        DateTimeOffset? DateTimeValue,
        HashSet<int> MatchingVersionIds);

    private sealed class SearchPartitionState
    {
        public SearchPartitionState(
            int blueprintVersionId,
            HashSet<ProcessStatus> processStatuses,
            string? continuationToken = null,
            bool isExhausted = false,
            List<ProcessStateSummary>? buffer = null)
        {
            BlueprintVersionId = blueprintVersionId;
            ProcessStatuses = processStatuses;
            ContinuationToken = continuationToken;
            IsExhausted = isExhausted;
            Buffer = buffer ?? [];
        }

        public int BlueprintVersionId { get; }
        public HashSet<ProcessStatus> ProcessStatuses { get; }
        public string? ContinuationToken { get; set; }
        public bool IsExhausted { get; set; }
        public List<ProcessStateSummary> Buffer { get; }
    }

    private sealed record ProcessSearchCursor(
        string FilterSignature,
        List<ProcessSearchCursorPartition> Partitions);

    private sealed record IndexedProcessSearchResult(
        ProcessSearchPageViewModel Page,
        bool SawProjectionRows);

    private sealed record ProcessIndexSearchCursor(
        string FilterSignature,
        string ProjectionContinuationToken);

    private sealed record ProcessSearchCursorPartition(
        int BlueprintVersionId,
        string? ContinuationToken,
        bool IsExhausted,
        List<ProcessHistoryCursorItem> BufferedItems);

    private enum SearchPropertyScope
    {
        GlobalState,
        NodeInput,
        NodeOutput,
    }

    private enum SearchPropertyType
    {
        String,
        Integer,
        Decimal,
        Boolean,
        Guid,
        Date,
        DateTime,
        Enum,
    }
}
