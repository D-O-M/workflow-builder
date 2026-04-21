using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Tests.TestSupport;
using Backend.Database.Relational.Entities;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Models.InputModels;
using Backend.Models.ViewModels;
using Backend.Repositories;
using Backend.Repositories.Processes;
using Shouldly;

namespace Backend.API.Tests;

public sealed class ProcessProjectionServiceTests
{
    [Test]
    public async Task GetProcessStepDetailAsync_returns_mapping_and_override_summaries()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var sourceDefinition = await SeedTaskDefinitionWithChannelsAsync(
            database.Context,
            "Lookup",
            null,
            ("TownResult", new Dictionary<string, PrimitiveTypes>
            {
                ["town"] = PrimitiveTypes.String,
            }));
        var targetDefinition = await SeedTaskDefinitionWithChannelsAsync(
            database.Context,
            "Recommend",
            new Dictionary<string, PrimitiveTypes>
            {
                ["mappedTown"] = PrimitiveTypes.String,
                ["mode"] = PrimitiveTypes.String,
            });

        var repository = new BlueprintRepository(database.Context);
        var sourceNodeId = Guid.NewGuid();
        var targetNodeId = Guid.NewGuid();
        var created = await repository.CreateBlueprint(
            new BlueprintInputModel
            {
                Name = "Explorer Blueprint",
                StartingBlueprintNodeId = sourceNodeId,
                Tasks =
                [
                    new BlueprintNodeInputModel
                    {
                        InstanceId = sourceNodeId,
                        TaskDefinitionId = sourceDefinition.Id,
                        AllowedMajor = 1,
                        AllowedMinor = 0,
                        Position = new CoordinatesViewModel { X = 120, Y = 80 },
                        OutgoingConnections =
                        [
                            new BlueprintConnectionInputModel
                            {
                                ConnectedBlueprintNodeId = targetNodeId,
                                ChannelName = "TownResult",
                                Anchor = 0,
                                Side = "left",
                                Position = 62,
                                PropertyMappings =
                                [
                                    new BlueprintPropertyMappingInputModel
                                    {
                                        SourceProperty = "town",
                                        TargetProperty = "mappedTown",
                                    }
                                ]
                            }
                        ]
                    },
                    new BlueprintNodeInputModel
                    {
                        InstanceId = targetNodeId,
                        TaskDefinitionId = targetDefinition.Id,
                        AllowedMajor = 1,
                        AllowedMinor = 0,
                        Position = new CoordinatesViewModel { X = 520, Y = 80 },
                        DesignTimeInputOverrides = new Dictionary<string, JsonElement>
                        {
                            ["mode"] = JsonSerializer.SerializeToElement("design-time")
                        }
                    }
                ]
            },
            CancellationToken.None);

        var processId = Guid.NewGuid();
        var processRecord = new ProcessInstanceRecord<Dictionary<string, JsonElement>>
        {
            id = processId.ToString(),
            BlueprintId = created.Versions[0].BlueprintVersionId.ToString(),
            CreatedAt = DateTimeOffset.Parse("2026-04-21T00:10:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-04-21T00:12:00Z"),
            Status = ProcessStatus.Pending,
            GlobalState = new Dictionary<string, JsonElement>(),
            NodeOverrides = new Dictionary<string, Dictionary<string, JsonElement>>
            {
                [targetNodeId.ToString()] = new Dictionary<string, JsonElement>
                {
                    ["mode"] = JsonSerializer.SerializeToElement("runtime")
                }
            }
        };

        var service = new ProcessProjectionService(
            database.Context,
            new ProcessStepRepositoryStub([]),
            new ProcessInstanceRepositoryStub(created.Versions[0].BlueprintVersionId, processRecord));

        var detail = await service.GetProcessStepDetailAsync(
            created.Id,
            processId,
            targetNodeId,
            CancellationToken.None);

        detail.IncomingMappings.Count.ShouldBe(1);
        detail.IncomingMappings[0].SourceBlueprintNodeId.ShouldBe(sourceNodeId);
        detail.IncomingMappings[0].SourceName.ShouldBe("Lookup");
        detail.IncomingMappings[0].ChannelName.ShouldBe("TownResult");
        detail.IncomingMappings[0].PropertyMappings.Count.ShouldBe(1);
        detail.IncomingMappings[0].PropertyMappings[0].SourceProperty.ShouldBe("town");
        detail.IncomingMappings[0].PropertyMappings[0].TargetProperty.ShouldBe("mappedTown");

        detail.DesignTimeInputOverrides.ShouldNotBeNull();
        detail.DesignTimeInputOverrides.Value.GetProperty("mode").GetString().ShouldBe("design-time");
        detail.RuntimeOverrides.ShouldNotBeNull();
        detail.RuntimeOverrides.Value.GetProperty("mode").GetString().ShouldBe("runtime");
    }

    private static async Task<TaskDefinition> SeedTaskDefinitionWithChannelsAsync(
        Backend.Database.Relational.BlueprintDbContext context,
        string name,
        Dictionary<string, PrimitiveTypes>? inputProperties,
        params (string ChannelName, Dictionary<string, PrimitiveTypes> Properties)[] outputChannels)
    {
        var taskDefinition = new TaskDefinition
        {
            Name = name,
            Versions =
            [
                new TaskVersion
                {
                    Version = new Version(1, 0, 0),
                    Path = $"/plugins/{name.ToLowerInvariant()}",
                    State = TaskVersionState.Active,
                    Channels = BuildChannels(inputProperties, outputChannels)
                }
            ]
        };

        context.TaskDefinitions.Add(taskDefinition);
        await context.SaveChangesAsync();
        return taskDefinition;
    }

    private sealed class ProcessInstanceRepositoryStub : IProcessInstanceRepository<Dictionary<string, JsonElement>>
    {
        private readonly int _blueprintVersionId;
        private readonly ProcessInstanceRecord<Dictionary<string, JsonElement>> _processRecord;

        public ProcessInstanceRepositoryStub(
            int blueprintVersionId,
            ProcessInstanceRecord<Dictionary<string, JsonElement>> processRecord)
        {
            _blueprintVersionId = blueprintVersionId;
            _processRecord = processRecord;
        }

        public Task<ProcessInstanceRecord<Dictionary<string, JsonElement>>> CreateProcessState(
            ProcessInstanceRecord<Dictionary<string, JsonElement>> state,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessInstanceRecord<Dictionary<string, JsonElement>>> GetProcessStateById(
            Guid processId,
            int blueprintVersionId,
            CancellationToken cancellationToken)
        {
            if (processId == Guid.Parse(_processRecord.ProcessId) && blueprintVersionId == _blueprintVersionId)
            {
                return Task.FromResult(_processRecord);
            }

            throw new EntityNotFoundException<ProcessInstanceRecord<Dictionary<string, JsonElement>>>(processId.ToString());
        }

        public Task<List<ProcessInstanceRecord<Dictionary<string, JsonElement>>>> GetProcessStatesForBlueprint(
            int blueprintVersionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessStateSummary?> GetLatestProcessStateForBlueprint(
            int blueprintVersionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessStateSummaryPage> GetProcessStateSummaryPageForBlueprint(
            int blueprintVersionId,
            string? continuationToken,
            int maxItemCount,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessStateSummaryPage> GetProcessStateSummaryPageForBlueprint(
            int blueprintVersionId,
            string? continuationToken,
            int maxItemCount,
            DateTimeOffset? createdFrom,
            DateTimeOffset? createdTo,
            IReadOnlyCollection<ProcessStatus>? statuses,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessInstanceRecord<Dictionary<string, JsonElement>>> UpdateProcessState(
            ProcessInstanceRecord<Dictionary<string, JsonElement>> state,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ProcessStepRepositoryStub : IProcessStepRepository
    {
        private readonly List<ProcessStepRecord> _records;

        public ProcessStepRepositoryStub(List<ProcessStepRecord> records)
        {
            _records = records;
        }

        public Task<ProcessStepRecord> CreateProcessStep(ProcessStepRecord state, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<(bool Created, ProcessStepRecord State)> CreateProcessStepIfNotExists(
            ProcessStepRecord state,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessStepRecord> GetProcessStepById(
            Guid taskInstanceId,
            Guid processId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<List<ProcessStepRecord>> GetProcessStepsForProcess(Guid processId, CancellationToken cancellationToken) =>
            Task.FromResult(_records);

        public Task<ProcessStepRecord> UpdateProcessStep(ProcessStepRecord state, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryUpdateProcessStep(ProcessStepRecord state, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static List<Channel> BuildChannels(
        Dictionary<string, PrimitiveTypes>? inputProperties,
        IReadOnlyCollection<(string ChannelName, Dictionary<string, PrimitiveTypes> Properties)> outputChannels)
    {
        var channels = new List<Channel>();

        if (inputProperties is not null)
        {
            channels.Add(new InputChannelDefinition
            {
                Name = "Input",
                Properties = inputProperties
                    .Select(property => new ChannelProperty
                    {
                        Name = property.Key,
                        Type = property.Value,
                    })
                    .ToList()
            });
        }

        channels.AddRange(outputChannels.Select(channel => new OutputChannelDefinition
        {
            Name = channel.ChannelName,
            Properties = channel.Properties
                .Select(property => new ChannelProperty
                {
                    Name = property.Key,
                    Type = property.Value,
                })
                .ToList()
        }));

        return channels;
    }
}
