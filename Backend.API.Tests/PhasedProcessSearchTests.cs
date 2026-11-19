using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Services;
using Backend.API.Tests.TestSupport;
using Backend.Database.Relational.Entities;
using Backend.Interfaces.Repositories;
using Shouldly;

namespace Backend.API.Tests;

public sealed class PhasedProcessSearchTests
{
    [Test]
    public async Task Name_search_across_definitions_returns_the_started_version_and_the_waiting_phase()
    {
        await using var db = await SqliteBlueprintDb.CreateAsync();
        var series = new PhasedBlueprintDefinition
        {
            Key = "release", DisplayName = "Release for review",
            Versions = [new() { Version = 3 }, new() { Version = 4 }]
        };
        var other = new PhasedBlueprintDefinition { Key = "staged", DisplayName = "Staged release" };
        db.Context.AddRange(series, other, new PhasedBlueprintDefinition { Key = "archive", DisplayName = "Archive" });
        await db.Context.SaveChangesAsync();
        var startedVersion = series.Versions.First();
        var row = new PhasedProcessSearchRow(
            Guid.NewGuid(), series.Id.ToString(), startedVersion.Id, DateTimeOffset.UtcNow, null, "WaitingForTrigger", "review",
            [new("prepare", 0, "Prepare", "Completed"), new("review", 1, "Review", "Pending")]);
        var reader = new RecordingReader(new([row], "page two"));
        var service = new PhasedProcessSearchService(db.Context, reader);

        var page = await service.SearchAsync(" RELEASE ", "waitingfortrigger", 1, null, CancellationToken.None);
        reader.Criteria!.DefinitionIds!.ShouldBe(new[] { series.Id, other.Id }.Order().Select(id => id.ToString()));
        reader.Criteria.Status.ShouldBe("WaitingForTrigger");
        var cutoff = reader.Criteria.CreatedTo;
        var item = page.Items.Single();
        item.PhasedBlueprintVersionNumber.ShouldBe(3);
        item.CurrentPhaseName.ShouldBe("Review");
        item.CurrentPhaseIndex.ShouldBe(1);
        item.CompletedPhaseCount.ShouldBe(1);
        item.PhaseCount.ShouldBe(2);
        page.ContinuationToken.ShouldNotBeNull();

        await service.SearchAsync("release", "WaitingForTrigger", 1, page.ContinuationToken, CancellationToken.None);
        reader.Criteria!.CreatedTo.ShouldBe(cutoff);
        reader.Token.ShouldBe("page two");
        await Should.ThrowAsync<InvalidDataException>(() =>
            service.SearchAsync("release", "Failed", 1, page.ContinuationToken, CancellationToken.None));
    }

    [Test]
    public async Task Exact_id_search_works_without_a_series_selection_and_unknown_names_require_no_Cosmos_search()
    {
        await using var db = await SqliteBlueprintDb.CreateAsync();
        var reader = new RecordingReader(new([], null));
        var service = new PhasedProcessSearchService(db.Context, reader);
        var id = Guid.NewGuid();
        var result = await service.SearchAsync("missing series", null, 25, null, CancellationToken.None);
        result.Items.ShouldBeEmpty();
        reader.Criteria.ShouldBeNull();

        await service.SearchAsync(id.ToString("B"), null, 25, null, CancellationToken.None);
        reader.Criteria!.ProcessId.ShouldBe(id);
        reader.Criteria.DefinitionIds.ShouldBeNull();
        result = await service.SearchAsync(null, null, 25, null, CancellationToken.None);
        result.ContinuationToken.ShouldBeNull();
        reader.Criteria.ProcessId.ShouldBeNull();
    }

    [Test]
    public async Task Invalid_filters_and_cursors_fail_before_querying_storage()
    {
        await using var db = await SqliteBlueprintDb.CreateAsync();
        var reader = new RecordingReader(new([], null));
        var service = new PhasedProcessSearchService(db.Context, reader);
        await Should.ThrowAsync<InvalidDataException>(() =>
            service.SearchAsync(null, "0", 25, null, CancellationToken.None));
        await Should.ThrowAsync<InvalidDataException>(() =>
            service.SearchAsync(null, null, 101, null, CancellationToken.None));
        await Should.ThrowAsync<InvalidDataException>(() =>
            service.SearchAsync(null, null, 25, "!invalid", CancellationToken.None));
        reader.Criteria.ShouldBeNull();
    }

    private sealed class RecordingReader(PhasedProcessSearchPage page) : IPhasedProcessSearchReader
    {
        public PhasedProcessSearchCriteria? Criteria { get; private set; }
        public string? Token { get; private set; }

        public Task<PhasedProcessSearchPage> ReadPageAsync(PhasedProcessSearchCriteria criteria,
            string? continuationToken, int pageSize, CancellationToken cancellationToken)
        {
            Criteria = criteria;
            Token = continuationToken;
            return Task.FromResult(page);
        }
    }
}
