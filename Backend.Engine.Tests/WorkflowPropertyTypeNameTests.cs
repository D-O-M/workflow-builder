using Backend.Database.Relational.Entities;
using Backend.Models;
using Shouldly;

namespace Backend.Engine.Tests;

public sealed class WorkflowPropertyTypeNameTests
{
    [Test]
    public void TryParse_accepts_supported_scalar_and_collection_kinds()
    {
        WorkflowPropertyTypeName.TryParse("String", out var scalarKind, out var scalarCollection).ShouldBeTrue();
        scalarKind.ShouldBe(WorkflowPropertyKind.String);
        scalarCollection.ShouldBeFalse();

        WorkflowPropertyTypeName.TryParse("Guid[]", out var collectionKind, out var isCollection).ShouldBeTrue();
        collectionKind.ShouldBe(WorkflowPropertyKind.Guid);
        isCollection.ShouldBeTrue();
    }

    [Test]
    public void TryParse_rejects_nested_and_unknown_collection_kinds()
    {
        WorkflowPropertyTypeName.TryParse("String[][]", out _, out _).ShouldBeFalse();
        WorkflowPropertyTypeName.TryParse("Object[]", out _, out _).ShouldBeFalse();
        WorkflowPropertyTypeName.TryParse("Dictionary", out _, out _).ShouldBeFalse();
    }

    [Test]
    public void Collection_helpers_convert_between_suffix_and_scalar_names()
    {
        WorkflowPropertyTypeName.ToCollectionKindName("Integer").ShouldBe("Integer[]");
        WorkflowPropertyTypeName.ToScalarKindName("Integer[]").ShouldBe("Integer");
    }
}
