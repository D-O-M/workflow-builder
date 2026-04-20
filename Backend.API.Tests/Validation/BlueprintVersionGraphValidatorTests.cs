using System;
using System.Linq;
using System.Threading.Tasks;
using Backend.API.Validation.Blueprints;
using Backend.Models.InputModels;
using Backend.Models.ViewModels;
using Shouldly;

namespace Backend.API.Tests.Validation;

public sealed class BlueprintVersionGraphValidatorTests
{
    private static BlueprintNodeInputModel Task(Guid id, params Guid[] outgoing)
        => new()
        {
            InstanceId = id,
            TaskDefinitionId = 1,
            AllowedMajor = 1,
            AllowedMinor = 0,
            Position = new CoordinatesViewModel { X = 0, Y = 0 },
            OutgoingConnections = outgoing
                .Select(o => new BlueprintConnectionInputModel
                {
                    ConnectedBlueprintNodeId = o,
                    ChannelName = "out"
                })
                .ToList()
        };

    private static BlueprintVersionInputModel Version(Guid start, params BlueprintNodeInputModel[] tasks)
        => new()
        {
            BlueprintDefinitionId = 1,
            StartingBlueprintNodeId = start,
            Tasks = tasks.ToList()
        };

    [Test]
    public async Task ValidateAsync_returns_error_when_cycle_exists()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var model = Version(a,
            Task(a, b),
            Task(b, a));

        var sut = new BlueprintVersionGraphValidator();
        var result = await sut.ValidateAsync(model);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "graph.cycle");
    }

    [Test]
    public async Task ValidateAsync_returns_error_when_task_is_unreachable_from_start()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        var model = Version(a,
            Task(a, b),
            Task(b),
            Task(c));

        var sut = new BlueprintVersionGraphValidator();
        var result = await sut.ValidateAsync(model);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "graph.unreachable");
    }

    [Test]
    public async Task ValidateAsync_returns_error_when_start_task_has_incoming_edge()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var model = Version(a,
            Task(a),
            Task(b, a));

        var sut = new BlueprintVersionGraphValidator();
        var result = await sut.ValidateAsync(model);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "graph.start.incoming");
    }

    [Test]
    public async Task ValidateAsync_returns_error_when_node_has_multiple_incoming_edges()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        var model = Version(a,
            Task(a, c),
            Task(b, c),
            Task(c));

        var sut = new BlueprintVersionGraphValidator();
        var result = await sut.ValidateAsync(model);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "graph.node.multipleIncoming");
    }

    [Test]
    public async Task ValidateAsync_returns_valid_for_simple_chain()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        var model = Version(a,
            Task(a, b),
            Task(b, c),
            Task(c));

        var sut = new BlueprintVersionGraphValidator();
        var result = await sut.ValidateAsync(model);

        result.IsValid.ShouldBeTrue();
        result.Errors.Count.ShouldBe(0);
    }
}
