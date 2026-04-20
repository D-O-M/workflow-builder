using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Tests.TestSupport;
using Backend.Database.Relational.Entities;
using Backend.Models.DomainModels;
using Backend.Models.InputModels;
using Backend.Models.ViewModels;
using Backend.Repositories;
using Shouldly;

namespace Backend.API.Tests;

public sealed class BlueprintRepositoryTests
{
    [Test]
    public async Task CreateBlueprint_persists_definition_display_name()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var taskDefinition = await SeedTaskDefinitionAsync(database.Context, "Lookup", new Version(1, 0, 0));
        var repository = new BlueprintRepository(database.Context);
        var firstNodeId = Guid.NewGuid();

        var created = await repository.CreateBlueprint(
            CreateBlueprintInput("Customer Intake", taskDefinition.Id, firstNodeId),
            CancellationToken.None);

        created.Name.ShouldBe("Customer Intake");
        var storedDefinition = await database.Context.BlueprintDefinitions.FindAsync(created.Id);
        storedDefinition.ShouldNotBeNull();
        storedDefinition.DisplayName.ShouldBe("Customer Intake");
    }

    [Test]
    public async Task CreateVersion_keeps_existing_definition_display_name()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var taskDefinition = await SeedTaskDefinitionAsync(database.Context, "Lookup", new Version(1, 0, 0));
        var repository = new BlueprintRepository(database.Context);
        var firstNodeId = Guid.NewGuid();

        var created = await repository.CreateBlueprint(
            CreateBlueprintInput("Immutable Blueprint", taskDefinition.Id, firstNodeId),
            CancellationToken.None);

        var nextNodeId = Guid.NewGuid();
        var createdVersion = await repository.CreateVersion(
            CreateBlueprintVersion(created.Id, taskDefinition.Id, nextNodeId),
            CancellationToken.None);

        createdVersion.BlueprintDefinitionId.ShouldBe(created.Id);
        var reloaded = await repository.GetBlueprintById(created.Id, CancellationToken.None);
        reloaded.Name.ShouldBe("Immutable Blueprint");
        reloaded.Versions.Count.ShouldBe(2);
        reloaded.Versions[0].BlueprintVersionId.ShouldBe(createdVersion.BlueprintVersionId);
        reloaded.Versions[0].Version.ShouldBe(2);
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
                        .Select(channelName => (Channel)new OutputChannelDefinition { Name = channelName })
                        .ToList()
                }
            ]
        };

        context.TaskDefinitions.Add(taskDefinition);
        await context.SaveChangesAsync();
        return taskDefinition;
    }

    private static BlueprintInputModel CreateBlueprintInput(string name, int taskDefinitionId, Guid nodeId)
    {
        var version = new Version(1, 0, 0);
        return new BlueprintInputModel
        {
            Name = name,
            StartingBlueprintNodeId = nodeId,
            Tasks =
            [
                CreateBlueprintNode(nodeId, taskDefinitionId, version)
            ]
        };
    }

    private static FullBlueprintVersion CreateBlueprintVersion(int definitionId, int taskDefinitionId, Guid nodeId)
    {
        var version = new Version(1, 0, 0);
        var (allowedMajor, allowedMinor) = GetLane(version);

        return new FullBlueprintVersion
        {
            DefinitionId = definitionId,
            TaskTree = new BlueprintTaskTree
            {
                InstanceId = nodeId,
                TaskDefinitionId = taskDefinitionId,
                AllowedMajor = allowedMajor,
                AllowedMinor = allowedMinor,
                Position = new Coordinates(180, 120),
            }
        };
    }

    private static BlueprintNodeInputModel CreateBlueprintNode(Guid nodeId, int taskDefinitionId, Version version)
    {
        var (allowedMajor, allowedMinor) = GetLane(version);
        return new BlueprintNodeInputModel
        {
            InstanceId = nodeId,
            TaskDefinitionId = taskDefinitionId,
            AllowedMajor = allowedMajor,
            AllowedMinor = allowedMinor,
            Position = new CoordinatesViewModel
            {
                X = 180,
                Y = 120,
            }
        };
    }

    private static (int AllowedMajor, int AllowedMinor) GetLane(Version version) => (version.Major, version.Minor);
}
