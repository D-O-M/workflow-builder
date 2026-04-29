using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Backend.API.Services;
using Backend.API.Tests.TestSupport;
using Backend.API.Validation.Blueprints;
using Backend.Database.Relational.Entities;
using Backend.Interfaces.Repositories;
using Backend.Models;
using Backend.Models.InputModels;
using Backend.Models.ViewModels;
using Backend.Repositories;
using Shouldly;

namespace Backend.API.Tests;

public sealed class BlueprintPortabilityServiceTests
{
    private const string TownSeedFileName = "showcase-town-weekend-recommendation.blueprint.json";
    private const string MunicipalSeedFileName = "showcase-municipal-readiness-advisor.blueprint.json";
    private const string DeliverySeedFileName = "showcase-cross-border-delivery-window-advisor.blueprint.json";
    private const string AirportSeedFileName = "showcase-airport-disruption-early-warning.blueprint.json";

    [Test]
    public async Task ExportLatestBlueprint_uses_schema_version_3_without_version_name()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var taskDefinition = await SeedTaskDefinitionAsync(database.Context, "Lookup", new Version(1, 0, 0));
        var repository = new BlueprintRepository(database.Context);
        var startingNodeId = Guid.NewGuid();
        var createdBlueprint = await repository.CreateBlueprint(
            new BlueprintInputModel
            {
                Name = "Customer Intake",
                Key = "customer-intake",
                StartingBlueprintNodeId = startingNodeId,
                Tasks =
                [
                    new BlueprintNodeInputModel
                    {
                        InstanceId = startingNodeId,
                        TaskDefinitionId = taskDefinition.Id,
                        AllowedMajor = 1,
                        AllowedMinor = 0,
                        Position = new CoordinatesViewModel
                        {
                            X = 120,
                            Y = 80,
                        }
                    }
                ]
            },
            CancellationToken.None);

        var service = new BlueprintPortabilityService(
            database.Context,
            new RecordingBlueprintRepository(),
            new AlwaysValidBlueprintVersionValidator());

        var exported = await service.ExportLatestBlueprintAsync(createdBlueprint.Id, CancellationToken.None);
        var json = JsonSerializer.Serialize(exported, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        exported.SchemaVersion.ShouldBe(3);
        json.ShouldContain("\"schemaVersion\":3");
        json.ShouldNotContain("versionName");
    }

    [Test]
    public async Task ExportLatestBlueprint_preserves_collection_iterator_nodes()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var taskDefinition = await SeedTaskDefinitionAsync(database.Context, "Lookup", new Version(1, 0, 0), "Items");
        var repository = new BlueprintRepository(database.Context);
        var startingNodeId = Guid.NewGuid();
        var iteratorNodeId = Guid.NewGuid();

        var createdBlueprint = await repository.CreateBlueprint(
            new BlueprintInputModel
            {
                Name = "Customer Intake",
                Key = "customer-intake",
                StartingBlueprintNodeId = startingNodeId,
                Tasks =
                [
                    new BlueprintNodeInputModel
                    {
                        InstanceId = startingNodeId,
                        TaskDefinitionId = taskDefinition.Id,
                        AllowedMajor = 1,
                        AllowedMinor = 0,
                        Position = new CoordinatesViewModel { X = 120, Y = 80 },
                        OutgoingConnections =
                        [
                            new BlueprintConnectionInputModel
                            {
                                ChannelName = "Items",
                                ConnectedBlueprintNodeId = iteratorNodeId,
                                Side = "right",
                                PropertyMappings =
                                [
                                    new BlueprintPropertyMappingInputModel
                                    {
                                        SourceProperty = "ids",
                                        TargetProperty = "items"
                                    }
                                ]
                            }
                        ]
                    },
                    new BlueprintNodeInputModel
                    {
                        InstanceId = iteratorNodeId,
                        NodeKind = BlueprintNodeKind.CollectionIterator.ToString(),
                        CollectionIteratorItemKind = "Guid",
                        AllowedMajor = 0,
                        AllowedMinor = 0,
                        Position = new CoordinatesViewModel { X = 420, Y = 80 }
                    }
                ]
            },
            CancellationToken.None);

        var service = new BlueprintPortabilityService(
            database.Context,
            new RecordingBlueprintRepository(),
            new AlwaysValidBlueprintVersionValidator());

        var exported = await service.ExportLatestBlueprintAsync(createdBlueprint.Id, CancellationToken.None);
        var iterator = exported.Tasks.Single(task => task.TaskKey == iteratorNodeId);

        iterator.NodeKind.ShouldBe(BlueprintNodeKind.CollectionIterator.ToString());
        iterator.TaskDefinitionName.ShouldBeNull();
        iterator.CollectionIteratorItemKind.ShouldBe("Guid");
        iterator.AllowedMajor.ShouldBe(0);
        iterator.AllowedMinor.ShouldBe(0);
    }

    [Test]
    public async Task ImportBlueprint_preserves_collection_iterator_nodes()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var startingNodeId = Guid.NewGuid();
        var iteratorNodeId = Guid.NewGuid();
        var seed = new ExportedBlueprintInputModel
        {
            SchemaVersion = 3,
            DefinitionKey = "customer-intake",
            DisplayName = "Customer Intake",
            StartingTaskKey = startingNodeId,
            Tasks =
            [
                new ExportedBlueprintNodeInputModel
                {
                    TaskKey = startingNodeId,
                    TaskDefinitionName = "Lookup",
                    AllowedMajor = 1,
                    AllowedMinor = 0,
                    Position = new CoordinatesViewModel { X = 120, Y = 80 },
                    OutgoingConnections =
                    [
                        new ExportedBlueprintConnectionInputModel
                        {
                            ChannelName = "Items",
                            ConnectedTaskKey = iteratorNodeId,
                            Side = "right",
                            PropertyMappings =
                            [
                                new BlueprintPropertyMappingInputModel
                                {
                                    SourceProperty = "ids",
                                    TargetProperty = "items"
                                }
                            ]
                        }
                    ]
                },
                new ExportedBlueprintNodeInputModel
                {
                    TaskKey = iteratorNodeId,
                    NodeKind = BlueprintNodeKind.CollectionIterator.ToString(),
                    CollectionIteratorItemKind = "Guid",
                    AllowedMajor = 0,
                    AllowedMinor = 0,
                    Position = new CoordinatesViewModel { X = 420, Y = 80 }
                }
            ]
        };
        await SeedImportTaskDefinitionsAsync(database.Context, seed);

        var repository = new RecordingBlueprintRepository();
        var service = new BlueprintPortabilityService(
            database.Context,
            repository,
            new AlwaysValidBlueprintVersionValidator());

        var imported = await service.ImportBlueprintAsync(seed, CancellationToken.None);
        var createdIterator = repository.CreatedBlueprints.Single().Tasks.Single(task => task.InstanceId == iteratorNodeId);

        imported.Status.ShouldBe(BlueprintImportStatus.CreatedDefinition);
        createdIterator.NodeKind.ShouldBe(BlueprintNodeKind.CollectionIterator.ToString());
        createdIterator.TaskDefinitionId.ShouldBeNull();
        createdIterator.CollectionIteratorItemKind.ShouldBe("Guid");
        createdIterator.AllowedMajor.ShouldBe(0);
        createdIterator.AllowedMinor.ShouldBe(0);
    }

    [Test]
    public async Task ImportBlueprint_returns_display_name_conflict_for_mismatched_existing_definition()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var seed = await LoadSeedBlueprintAsync(TownSeedFileName, ignoreImportedDisplayNameMismatch: false);
        await SeedImportTaskDefinitionsAsync(database.Context, seed);
        database.Context.BlueprintDefinitions.Add(new BlueprintDefinition
        {
            Key = "showcase-town-weekend-recommendation",
            DisplayName = "Stored Blueprint Name",
        });
        await database.Context.SaveChangesAsync();

        var repository = new RecordingBlueprintRepository();
        var service = new BlueprintPortabilityService(
            database.Context,
            repository,
            new AlwaysValidBlueprintVersionValidator());

        var imported = await service.ImportBlueprintAsync(
            seed,
            CancellationToken.None);

        imported.Status.ShouldBe(BlueprintImportStatus.DisplayNameConflict);
        imported.StoredDisplayName.ShouldBe("Stored Blueprint Name");
        imported.ImportedDisplayName.ShouldBe("Showcase Town Weekend Recommendation");
        imported.CanIgnoreImportedDisplayNameMismatch.ShouldBeTrue();
        repository.CreatedBlueprints.Count.ShouldBe(0);
        repository.CreatedVersions.Count.ShouldBe(0);
    }

    [Test]
    public async Task ImportBlueprint_with_ignore_flag_preserves_stored_name_and_creates_version()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var seed = await LoadSeedBlueprintAsync(TownSeedFileName, ignoreImportedDisplayNameMismatch: true);
        await SeedImportTaskDefinitionsAsync(database.Context, seed);
        var existingDefinition = new BlueprintDefinition
        {
            Key = "showcase-town-weekend-recommendation",
            DisplayName = "Stored Blueprint Name",
        };

        database.Context.BlueprintDefinitions.Add(existingDefinition);
        await database.Context.SaveChangesAsync();

        var repository = new RecordingBlueprintRepository();
        var service = new BlueprintPortabilityService(
            database.Context,
            repository,
            new AlwaysValidBlueprintVersionValidator());

        var imported = await service.ImportBlueprintAsync(
            seed,
            CancellationToken.None);

        imported.Status.ShouldBe(BlueprintImportStatus.CreatedVersion);
        imported.BlueprintDefinitionId.ShouldBe(existingDefinition.Id);
        repository.CreatedVersions.Count.ShouldBe(1);
        repository.CreatedBlueprints.Count.ShouldBe(0);
        existingDefinition.DisplayName.ShouldBe("Stored Blueprint Name");
    }

    [Test]
    public async Task ImportBlueprint_creates_definition_for_municipal_seed()
    {
        await AssertSeedImportCreatesDefinitionAsync(MunicipalSeedFileName);
    }

    [Test]
    public async Task ImportBlueprint_creates_definition_for_delivery_seed()
    {
        await AssertSeedImportCreatesDefinitionAsync(DeliverySeedFileName);
    }

    [Test]
    public async Task ImportBlueprint_creates_definition_for_airport_seed()
    {
        await AssertSeedImportCreatesDefinitionAsync(AirportSeedFileName);
    }

    private static async Task<TaskDefinition> SeedTaskDefinitionAsync(
        Backend.Database.Relational.BlueprintDbContext context,
        string name,
        Version version,
        params string[] outputChannels)
    {
        var taskDefinition = new TaskDefinition
        {
            Name = name,
            Versions =
            [
                new TaskVersion
                {
                    Version = version,
                    Path = $"/plugins/{name.ToLowerInvariant()}",
                    State = TaskVersionState.Active,
                    Channels = outputChannels
                        .Select(channelName => (Backend.Database.Relational.Entities.Channel)new OutputChannelDefinition { Name = channelName })
                        .ToList()
                }
            ]
        };

        context.TaskDefinitions.Add(taskDefinition);
        await context.SaveChangesAsync();
        return taskDefinition;
    }

    private static async Task SeedImportTaskDefinitionsAsync(
        Backend.Database.Relational.BlueprintDbContext context,
        ExportedBlueprintInputModel model)
    {
        var seededTaskNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var task in model.Tasks)
        {
            if (!string.Equals(task.NodeKind, BlueprintNodeKind.PluginTask.ToString(), StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(task.TaskDefinitionName) ||
                !seededTaskNames.Add(task.TaskDefinitionName))
            {
                continue;
            }

            var outputChannels = task.OutgoingConnections
                .Select(connection => connection.ChannelName)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            await SeedTaskDefinitionAsync(
                context,
                task.TaskDefinitionName,
                new Version(task.AllowedMajor, task.AllowedMinor, 1),
                outputChannels);
        }
    }

    private static async Task<ExportedBlueprintInputModel> LoadSeedBlueprintAsync(
        string seedFileName,
        bool ignoreImportedDisplayNameMismatch)
    {
        var jsonPath = FindSeedBlueprintPath(seedFileName);
        var json = await File.ReadAllTextAsync(jsonPath);
        var model = JsonSerializer.Deserialize<ExportedBlueprintInputModel>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        model.ShouldNotBeNull();
        return new ExportedBlueprintInputModel
        {
            SchemaVersion = model.SchemaVersion,
            DefinitionKey = model.DefinitionKey,
            DisplayName = model.DisplayName,
            StartingTaskKey = model.StartingTaskKey,
            Tasks = model.Tasks,
            IgnoreImportedDisplayNameMismatch = ignoreImportedDisplayNameMismatch,
        };
    }

    private static async Task AssertSeedImportCreatesDefinitionAsync(string seedFileName)
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var seed = await LoadSeedBlueprintAsync(seedFileName, ignoreImportedDisplayNameMismatch: false);
        await SeedImportTaskDefinitionsAsync(database.Context, seed);

        var repository = new RecordingBlueprintRepository();
        var service = new BlueprintPortabilityService(
            database.Context,
            repository,
            new AlwaysValidBlueprintVersionValidator());

        var imported = await service.ImportBlueprintAsync(seed, CancellationToken.None);

        imported.Status.ShouldBe(BlueprintImportStatus.CreatedDefinition);
        imported.BlueprintKey.ShouldBe(seed.DefinitionKey);
        repository.CreatedBlueprints.Count.ShouldBe(1);
        repository.CreatedVersions.Count.ShouldBe(0);
        repository.CreatedBlueprints.Single().Tasks.Count.ShouldBe(seed.Tasks.Count);
    }

    private static string FindSeedBlueprintPath(
        string seedFileName,
        [CallerFilePath] string sourceFilePath = "")
    {
        var relativeSeedPath = Path.Combine("SeedData", "Blueprints", seedFileName);

        var candidateRoots = new List<string?>
        {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
            Path.GetDirectoryName(sourceFilePath),
        };

        foreach (var startPath in candidateRoots.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var directory = new DirectoryInfo(startPath!);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, relativeSeedPath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException($"Could not locate {relativeSeedPath} from the test execution directories.");
    }

    private sealed class AlwaysValidBlueprintVersionValidator : IBlueprintVersionValidator
    {
        public Task<BlueprintValidationResult> ValidateAsync(BlueprintVersionInputModel model, CancellationToken ct = default) =>
            Task.FromResult(new BlueprintValidationResult());
    }

    private sealed class RecordingBlueprintRepository : IBlueprintRepository
    {
        public List<BlueprintInputModel> CreatedBlueprints { get; } = [];
        public List<BlueprintVersionInputModel> CreatedVersions { get; } = [];

        public Task<ExecutableBlueprint> GetFullBlueprintById(int id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> GetLatestBlueprintVersionId(int blueprintDefinitionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<List<BlueprintViewModel>> GetAllBlueprints(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BlueprintViewModel> GetBlueprintById(int id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BlueprintViewModel> CreateBlueprint(BlueprintInputModel model, CancellationToken cancellationToken)
        {
            CreatedBlueprints.Add(model);
            return Task.FromResult(new BlueprintViewModel
            {
                Id = 81,
                Key = model.Key ?? model.Name.ToLowerInvariant().Replace(' ', '-'),
                Name = model.Name,
                Description = string.Empty,
                Versions =
                [
                    new BlueprintVersionViewModel
                    {
                        BlueprintDefinitionId = 81,
                        BlueprintVersionId = 810,
                        Version = 1,
                        StartingBlueprintNodeId = model.StartingBlueprintNodeId,
                        Tasks = []
                    }
                ]
            });
        }

        public Task<BlueprintVersionViewModel> CreateVersion(BlueprintVersionInputModel model, CancellationToken cancellationToken)
        {
            CreatedVersions.Add(model);
            return Task.FromResult(new BlueprintVersionViewModel
            {
                BlueprintDefinitionId = model.BlueprintDefinitionId,
                BlueprintVersionId = 910,
                Version = 2,
                StartingBlueprintNodeId = model.StartingBlueprintNodeId,
                Tasks = []
            });
        }
    }
}
