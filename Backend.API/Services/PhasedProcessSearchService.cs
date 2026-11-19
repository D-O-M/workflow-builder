using System.Globalization;
using System.Text.Json;
using Backend.Database.Relational;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Models.ViewModels;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Backend.API.Services;

public sealed class PhasedProcessSearchService(BlueprintDbContext context, IPhasedProcessSearchReader reader)
{
    public async Task<PhasedProcessSearchPageViewModel> SearchAsync(
        string? q, string? status, int pageSize, string? continuationToken, CancellationToken cancellationToken)
    {
        if (pageSize is < 1 or > 100)
            throw new InvalidDataException("Page size must be between 1 and 100.");
        var text = q?.Trim().ToLowerInvariant() ?? "";
        if (text.Length > 200)
            throw new InvalidDataException("Search terms must be at most 200 characters.");
        var statusName = string.IsNullOrWhiteSpace(status) ? null : Enum.GetNames<PhasedProcessStatus>()
            .FirstOrDefault(candidate => candidate.Equals(status.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Unknown Phase Series status.");

        Guid? processId = Guid.TryParse(text, out var parsed) ? parsed : null;
        var filter = new SearchFilter(processId?.ToString() ?? text, statusName);
        var cursor = ReadCursor(continuationToken, filter);
        var upperBound = cursor?.UpperBound ?? DateTimeOffset.UtcNow;

        string[]? definitionIds = null;
        if (text.Length > 0 && processId is null)
        {
            var ids = await context.PhasedBlueprintDefinitions.AsNoTracking()
                .Where(definition => definition.DisplayName.ToLower().Contains(text))
                .OrderBy(definition => definition.Id)
                .Select(definition => definition.Id)
                .ToListAsync(cancellationToken);
            if (ids.Count == 0)
                return new([], null);
            definitionIds = ids.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray();
        }

        var criteria = new PhasedProcessSearchCriteria(definitionIds, processId, statusName, upperBound);
        var page = await reader.ReadPageAsync(criteria, cursor?.Token, pageSize, cancellationToken);
        var versionIds = page.Items.Select(row => row.PhasedBlueprintVersionId).Distinct().ToArray();
        var versions = await context.PhasedBlueprintVersions.AsNoTracking()
            .Where(version => versionIds.Contains(version.Id))
            .Select(version => new { version.Id, version.Version, version.PhasedBlueprintDefinition.DisplayName })
            .ToDictionaryAsync(version => version.Id, cancellationToken);

        var items = page.Items.Select(row =>
        {
            var currentPhase = row.Phases.FirstOrDefault(phase => phase.PhaseId == row.CurrentPhaseId);
            var version = versions[row.PhasedBlueprintVersionId];
            return new PhasedProcessSummaryViewModel(
                row.Id, int.Parse(row.PhasedBlueprintDefinitionId, CultureInfo.InvariantCulture), version.DisplayName,
                row.PhasedBlueprintVersionId, version.Version, row.Status, currentPhase?.PhaseName, currentPhase?.PhaseIndex,
                row.Phases.Count, row.Phases.Count(phase => phase.Status == nameof(PhasedProcessStatus.Completed)),
                row.CreatedAt, row.UpdatedAt);
        }).ToArray();
        var next = page.ContinuationToken is null ? null : WebEncoders.Base64UrlEncode(
            JsonSerializer.SerializeToUtf8Bytes(new SearchCursor(filter, upperBound, page.ContinuationToken)));
        return new(items, next);
    }

    private static SearchCursor? ReadCursor(string? token, SearchFilter filter)
    {
        if (token is null)
            return null;
        try
        {
            var cursor = JsonSerializer.Deserialize<SearchCursor>(WebEncoders.Base64UrlDecode(token));
            if (cursor?.Filter == filter && cursor.UpperBound != default && !string.IsNullOrWhiteSpace(cursor.Token))
                return cursor;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new InvalidDataException("Invalid Phase Series search cursor.", exception);
        }
        throw new InvalidDataException("The search cursor is invalid for these filters.");
    }

    private sealed record SearchFilter(string Text, string? Status);
    private sealed record SearchCursor(SearchFilter Filter, DateTimeOffset UpperBound, string Token);
}
