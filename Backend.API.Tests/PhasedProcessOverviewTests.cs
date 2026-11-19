using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Services;
using Backend.API.Tests.TestSupport;
using Backend.Database.Relational;
using Backend.Database.Relational.Entities;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Repositories;
using Shouldly;

namespace Backend.API.Tests;

public sealed class PhasedProcessOverviewTests
{
    private const string PreciseOutput = """{"values":[9223372036854775807,0.1234567890123456789012345678]}""";

    [Test]
    public async Task Overview_reads_the_recorded_versions_and_waiting_destination_without_a_child()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var sample = await SeedAsync(database.Context);
        var overview = await sample.Service.GetOverviewAsync(1, sample.ParentId, CancellationToken.None);

        sample.Reader.FullReads.ShouldBe(0);
        overview.PhasedBlueprintVersionNumber.ShouldBe(1);
        overview.CurrentPhaseId.ShouldBe(sample.Destination);
        overview.Status.ShouldBe("WaitingForTrigger");
        overview.Phases.Select(p => p.PhaseId).ToArray().ShouldBe(new[] { sample.Source, sample.Destination });
        overview.Phases[0].VersionPolicy.ShouldBe("Latest");
        overview.Phases[0].BlueprintVersionNumber.ShouldBe(2);
        overview.Phases[1].BlueprintVersionNumber.ShouldBeNull();
        overview.Phases[1].ChildProcessId.ShouldBeNull();
        overview.Transitions.Single().SourcePhaseId.ShouldBe(sample.Source);
        JsonSerializer.Serialize(overview).ShouldNotContain("Secret");
        await Should.ThrowAsync<EntityNotFoundException<PhasedBlueprintTransition>>(() =>
            sample.Service.GetHandoffAsync(1, sample.ParentId, sample.Destination, CancellationToken.None));
    }

    [Test]
    public async Task Handoff_uses_the_destination_node_key_and_distinguishes_null_missing_and_alias_only_values()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var sample = await SeedAsync(database.Context);
        var handoff = await sample.Service.GetHandoffAsync(1, sample.ParentId, sample.Source, CancellationToken.None);

        sample.Reader.FullReads.ShouldBe(1);
        var count = handoff.Mappings.Single(m => m.SourceProperty == "Count");
        count.HasValue.ShouldBeTrue();
        count.ValueJson.ShouldBe("0");
        count.SourceNodeName.ShouldBe("Source task");
        count.TargetNodeName.ShouldBe("Destination task");
        var optional = handoff.Mappings.Single(m => m.SourceProperty == "Optional");
        optional.HasValue.ShouldBeTrue();
        optional.ValueJson.ShouldBe("null");
        handoff.Mappings.Single(m => m.SourceProperty == "Metrics").ValueJson.ShouldBe(PreciseOutput);
        var missing = handoff.Mappings.Single(m => m.SourceProperty == "Other");
        missing.HasValue.ShouldBeFalse();
        missing.ValueJson.ShouldBeNull();
        var legacy = handoff.UnattributedValues.Single();
        legacy.Property.ShouldBe("Legacy");
        legacy.ValueJson.ShouldBe("false");
        JsonSerializer.Serialize(handoff).ShouldNotContain("Secret");
    }

    private static async Task<Sample> SeedAsync(BlueprintDbContext db)
    {
        var source = Guid.NewGuid();
        var destination = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var sourceNode = CreateNode("Source task");
        var firstTarget = CreateNode("Destination task");
        var secondTarget = CreateNode("Other destination");
        var blueprint = new BlueprintDefinition { Key = "example", DisplayName = "Example" };
        var executedVersion = new BlueprintVersion
        {
            BlueprintDefinition = blueprint, Version = 2, BlueprintNodes = [sourceNode, firstTarget, secondTarget]
        };
        db.BlueprintVersions.AddRange(executedVersion, new BlueprintVersion { BlueprintDefinition = blueprint, Version = 3 });
        var definition = new PhasedBlueprintDefinition { Id = 1, Key = "series", DisplayName = "Series" };
        var version = new PhasedBlueprintVersion { PhasedBlueprintDefinition = definition, Version = 1 };
        version.Phases =
        [
            new() { Id = source, Name = "Gather", PhaseIndex = 0, BlueprintDefinition = blueprint, VersionPolicy = PhasedBlueprintPhaseVersionPolicy.Latest },
            new() { Id = destination, Name = "Apply", PhaseIndex = 1, BlueprintDefinition = blueprint, VersionPolicy = PhasedBlueprintPhaseVersionPolicy.Latest }
        ];
        version.Transitions =
        [
            new()
            {
                Id = Guid.NewGuid(), SourcePhaseId = source, DestinationPhaseId = destination,
                HandoffMappings =
                [
                    Map(sourceNode.Id, "Count", firstTarget.Id, "Score"),
                    Map(sourceNode.Id, "Optional", secondTarget.Id, "Score"),
                    Map(sourceNode.Id, "Other", firstTarget.Id, "Legacy"),
                    Map(sourceNode.Id, "Metrics", firstTarget.Id, "Stats")
                ]
            }
        ];
        db.PhasedBlueprintVersions.AddRange(version, new PhasedBlueprintVersion { PhasedBlueprintDefinition = definition, Version = 2 });
        await db.SaveChangesAsync();
        var parent = new PhasedProcessRecord
        {
            id = parentId.ToString(), PhasedBlueprintDefinitionId = "1", PhasedBlueprintVersionId = version.Id,
            CurrentPhaseId = destination.ToString(), Status = PhasedProcessStatus.WaitingForTrigger,
            Phases =
            [
                new() { PhaseId = destination.ToString(), PhaseIndex = 1, PhaseName = "Apply", BlueprintDefinitionId = blueprint.Id, Status = PhasedProcessStatus.Pending },
                new() { PhaseId = source.ToString(), PhaseIndex = 0, PhaseName = "Gather", BlueprintDefinitionId = blueprint.Id,
                    BlueprintVersionId = executedVersion.Id, Status = PhasedProcessStatus.Completed, ChildProcessId = Guid.NewGuid().ToString(),
                    TriggerInput = { ["Secret"] = JsonSerializer.SerializeToElement("private") } }
            ],
            PhaseResults =
            {
                [source.ToString()] = new()
                {
                    [$"{firstTarget.Id:N}:Score"] = JsonSerializer.SerializeToElement(0),
                    [$"{secondTarget.Id:N}:Score"] = JsonSerializer.SerializeToElement<object?>(null),
                    ["Score"] = JsonSerializer.SerializeToElement(0),
                    ["Legacy"] = JsonSerializer.SerializeToElement(false),
                    [$"{firstTarget.Id:N}:Stats"] = JsonSerializer.Deserialize<JsonElement>(PreciseOutput)
                }
            }
        };
        var reader = new ParentReader(parent);
        return new Sample(source, destination, parentId, reader, new PhasedProcessOverviewService(reader, db));
    }

    private static BlueprintNode CreateNode(string name) => new()
    {
        Id = Guid.NewGuid(), TaskDefinition = new TaskDefinition { Name = name, Versions = [] }
    };

    private static PhasedBlueprintHandoffMapping Map(Guid source, string sourceProperty, Guid target, string targetProperty) =>
        new() { SourceBlueprintNodeId = source, SourceChannelName = "Success", SourceProperty = sourceProperty,
            TargetBlueprintNodeId = target, TargetProperty = targetProperty };

    private sealed record Sample(Guid Source, Guid Destination, Guid ParentId, ParentReader Reader, PhasedProcessOverviewService Service);

    private sealed class ParentReader(PhasedProcessRecord parent) : IPhasedProcessRepository
    {
        public int FullReads { get; private set; }
        public Task<PhasedProcessOverviewState> GetOverviewAsync(Guid id, int definition, CancellationToken ct)
        {
            if (parent.Id != id.ToString() || parent.PhasedBlueprintDefinitionId != definition.ToString())
                throw new EntityNotFoundException<PhasedProcessRecord>(id.ToString());
            return Task.FromResult(new PhasedProcessOverviewState(parent.PhasedBlueprintVersionId, parent.Status.ToString(),
                parent.CreatedAt, parent.UpdatedAt, parent.CurrentPhaseId, parent.Phases.Select(p => new PhasedProcessOverviewPhase(
                    p.PhaseId, p.PhaseIndex, p.PhaseName, p.BlueprintDefinitionId, p.BlueprintVersionId, p.ChildProcessId,
                    p.Status.ToString(), p.StartedAt, p.CompletedAt, p.FailureMessage)).ToList()));
        }
        public Task<PhasedProcessRecord> GetAsync(Guid id, int definition, CancellationToken ct) =>
            GetAsync(id, definition.ToString(), ct);
        public Task<PhasedProcessRecord> GetAsync(Guid id, string definition, CancellationToken ct)
        {
            FullReads++;
            return Task.FromResult(parent.Id == id.ToString() && parent.PhasedBlueprintDefinitionId == definition
                ? parent : throw new EntityNotFoundException<PhasedProcessRecord>(id.ToString()));
        }
        public Task<PhasedProcessRecord> CreateAsync(PhasedProcessRecord record, CancellationToken ct) => throw new NotSupportedException();
        public Task<PhasedProcessRecord> UpdateAsync(PhasedProcessRecord record, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> TryUpdateAsync(PhasedProcessRecord record, CancellationToken ct) => throw new NotSupportedException();
    }
}
