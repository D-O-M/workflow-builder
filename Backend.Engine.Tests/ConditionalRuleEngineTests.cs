using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Backend.Models;
using Shouldly;

namespace Backend.Engine.Tests;

public sealed class ConditionalRuleEngineTests
{
    [Test]
    public void Compound_rule_round_trips_and_respects_group_order_and_negation()
    {
        var fields = new[] { Field("country", "String"), Field("urgent", "Boolean"), Field("amount", "Decimal") };
        var rule = new ConditionalRule
        {
            Kind = "all",
            Children = [
                new() { Kind = "any", Children = [Compare("country", "startsWith", "GB") with { IgnoreCase = true }, Compare("urgent", "isTrue")] },
                Compare("amount", "lt", "100") with { Negated = true }
            ]
        };
        var copy = JsonSerializer.Deserialize<ConditionalRule>(JsonSerializer.Serialize(rule, JsonSerializerOptions.Web), JsonSerializerOptions.Web);
        var input = Values(new { country = "gb-north", amount = 100 });
        ConditionalRuleEngine.Evaluate(copy, fields, input).ShouldBeTrue();
        input["amount"] = JsonSerializer.SerializeToElement(99.99m);
        ConditionalRuleEngine.Evaluate(copy, fields, input).ShouldBeFalse();
        input["country"] = JsonSerializer.SerializeToElement("FR");
        Should.Throw<InvalidDataException>(() => ConditionalRuleEngine.Evaluate(copy, fields, input))
            .Message.ShouldContain("rule.children[0].children[1]");
    }

    [Test]
    [Arguments("Integer","18446744073709551615","gt","18446744073709551614",true)]
    [Arguments("Decimal","9007199254740993.000000000001","eq","9007199254740993.000000000002",false)]
    [Arguments("Decimal","1.0E+280","eq","10e279",true)]
    [Arguments("Decimal","0.125","eq","+.125",true)]
    [Arguments("Integer","-1","gte","-1",true)]
    [Arguments("Decimal","1.00","lte","1",true)]
    [Arguments("Date","\"2024-02-29\"","gt","2024-02-28",true)]
    [Arguments("DateTime","\"2026-10-25T01:05:00+01:00\"","eq","2026-10-25T00:05:00Z",true)]
    [Arguments("DateTime","\"2026-10-25T00:05:00\"","eq","2026-10-25T00:05:00Z",true)]
    [Arguments("Guid","\"a637a137-5d19-4240-aac0-754ea5b775fb\"","isOneOf","A637A137-5D19-4240-AAC0-754EA5B775FB",true)]
    [Arguments("String","\"abc\"","endsWith","BC",false)]
    [Arguments("String","\"abc\"","contains","b",true)]
    public void Scalars_compare_using_the_declared_type(string kind, string sourceJson, string op, string literal, bool expected)
    {
        var rule = Compare("value", op, literal);
        var fields = new[] { Field("value", kind) };
        var values = new Dictionary<string, JsonElement> { ["value"] = JsonDocument.Parse(sourceJson).RootElement.Clone() };
        ConditionalRuleEngine.Evaluate(rule, fields, values).ShouldBe(expected);
    }

    [Test]
    public void Numeric_fields_compare_across_types_with_inclusive_bounds()
    {
        var rule = Compare("amount", "between", "99", "100");
        rule.Operands[1] = new() { Field = "limit" };
        ConditionalRuleEngine.Evaluate(rule, [Field("amount", "Decimal"), Field("limit", "Integer")],
            Values(new { amount = 100.0m, limit = 100 })).ShouldBeTrue();
    }

    [Test]
    public void Collection_comparisons_use_element_type_and_distinct_membership()
    {
        var fields = new[] { Field("counts", "Integer[]"), Field("sought", "Decimal") };
        var input = Values(new { counts = new[] { 1, 1, 2 }, sought = 3m });
        var rule = Compare("counts", "containsAll", "1", "2");
        ConditionalRuleEngine.Evaluate(rule, fields, input).ShouldBeTrue();
        rule.Operands[0] = new() { Field = "sought" };
        ConditionalRuleEngine.Evaluate(rule, fields, input).ShouldBeFalse();
        ConditionalRuleEngine.Evaluate(rule with { Operator = "containsAny" }, fields, input).ShouldBeTrue();
        ConditionalRuleEngine.Evaluate(Compare("counts", "countEq", "3"), fields, input).ShouldBeTrue();
        var badInput = Values(new { counts = new object[] { 1, "private-value" } });
        var message = Should.Throw<InvalidDataException>(() => ConditionalRuleEngine.Evaluate(Compare("counts", "contains", "1"), fields, badInput)).Message;
        message.ShouldContain("counts");
        message.ShouldNotContain("private-value");
    }

    [Test]
    public void Presence_guards_can_short_circuit_missing_and_null_fields()
    {
        var fields = new[] { Field("value", "String") };
        var rule = new ConditionalRule { Kind = "all", Children = [Compare("value", "hasValue"), Compare("value", "isBlank")] };
        foreach (var values in new[] { Values(new { }), Values(new { value = (object?)null }) })
        {
            ConditionalRuleEngine.Evaluate(rule, fields, values).ShouldBeFalse();
            ConditionalRuleEngine.Evaluate(Compare("value", "hasNoValue"), fields, values).ShouldBeTrue();
            Should.Throw<InvalidDataException>(() => ConditionalRuleEngine.Evaluate(Compare("value", "isBlank"), fields, values));
        }
        ConditionalRuleEngine.Evaluate(rule, fields, Values(new { value = " \t" })).ShouldBeTrue();
        ConditionalRuleEngine.Evaluate(Compare("value", "isEmpty"), fields, Values(new { value = "" })).ShouldBeTrue();
    }

    [Test]
    public void Collection_field_operands_expand_typed_items_and_handle_empty_collections()
    {
        var fields = new[] { Field("scores", "Decimal[]"), Field("expected", "Integer[]"), Field("labels", "String[]") };
        var input = Values(new { scores = new[] { 1.00m, 3m }, expected = new[] { 1, 3 } });
        var rule = Compare("scores", "containsAll");
        rule.Operands = [new() { Field = "expected" }, new() { Literal = JsonSerializer.SerializeToElement("0.30e1") }];
        ConditionalRuleEngine.Evaluate(rule, fields, input).ShouldBeTrue();
        rule.Operands.RemoveAt(1);
        input["expected"] = JsonSerializer.SerializeToElement(Array.Empty<int>());
        ConditionalRuleEngine.Evaluate(rule, fields, input).ShouldBeTrue();
        ConditionalRuleEngine.Evaluate(rule with { Operator = "containsAny" }, fields, input).ShouldBeFalse();
        ConditionalRuleEngine.Validate(rule with { Operator = "contains" }, fields).Single().Path.ShouldBe("rule.operands[0].field");
        rule.Operands[0] = new() { Field = "labels" };
        ConditionalRuleEngine.Validate(rule, fields).Single().Path.ShouldBe("rule.operands[0].field");
    }

    [Test]
    public void Operand_wire_reads_legacy_nulls_and_writes_only_the_selected_source()
    {
        var options = JsonSerializerOptions.Web;
        var fieldOperand = JsonSerializer.Deserialize<ConditionalOperand>("""{"field":"second","literal":null}""", options)!;
        var literalOperand = JsonSerializer.Deserialize<ConditionalOperand>("""{"field":null,"literal":false}""", options)!;
        JsonSerializer.Serialize(fieldOperand, options).ShouldBe("""{"field":"second"}""");
        JsonSerializer.Serialize(literalOperand, options).ShouldBe("""{"literal":false}""");
        var numericOperand = JsonSerializer.Deserialize<ConditionalOperand>("""{"literal":9007199254740993.0000000001}""", options)!;
        numericOperand.Literal!.Value.ValueKind.ShouldBe(JsonValueKind.Number);
        JsonSerializer.Serialize(numericOperand, options).ShouldBe("""{"literal":"9007199254740993.0000000001"}""");
        var missing = JsonSerializer.Deserialize<ConditionalOperand>("""{"literal":null}""", options)!;
        var rule = Compare("first", "eq") with { Operands = [missing] };
        ConditionalRuleEngine.Validate(rule, [Field("first", "Boolean")]).Single().Path.ShouldBe("rule.operands[0]");
    }

    [Test]
    public void Rejects_unsupported_operators_incompatible_field_references_and_unknown_enum_members()
    {
        var fields = new[] { Field("value", "Enum") with { EnumValues = ["Gold", "Silver"] },
            Field("other", "Enum") with { EnumValues = ["Silver", "Blue"] } };
        var rule = Compare("value", "eq", "secret");
        ConditionalRuleEngine.Validate(rule, fields).Single().Path.ShouldBe("rule.operands[0].literal");
        rule.Operands[0] = new() { Field = "other" };
        ConditionalRuleEngine.Validate(rule, fields).Single().Path.ShouldBe("rule.operands[0].field");
        ConditionalRuleEngine.Validate(Compare("value", "startsWith", "a"), fields).Single().Path.ShouldBe("rule.operator");
        ConditionalRuleEngine.Evaluate(Compare("value", "isOneOf", "Silver", "Gold"), fields, Values(new { value = "Gold" })).ShouldBeTrue();
        Should.Throw<InvalidDataException>(() => ConditionalRuleEngine.Evaluate(Compare("value", "eq", "Gold"), fields, Values(new { value = 0 })));
        ConditionalRuleEngine.Validate(Compare("value", "eq", "Gold") with { IgnoreCase = true }, fields).Single().Path.ShouldBe("rule.ignoreCase");
    }

    [Test]
    public void Invalid_and_oversized_trees_fail_validation_with_rule_paths()
    {
        var fields = new[] { Field("value", "Boolean") };
        var leaf = Compare("value", "isFalse");
        var root = leaf;
        for (var i = 0; i < 8; i++)
            root = new() { Kind = "all", Children = [root] };
        ConditionalRuleEngine.Validate(root, fields).Single().Path.ShouldEndWith(".children[0]");
        var wide = new ConditionalRule { Kind = "any", Children = Enumerable.Repeat(leaf, 128).ToList() };
        ConditionalRuleEngine.Validate(wide, fields).Single().Path.ShouldBe("rule.children[127]");
        ConditionalRuleEngine.Validate(new() { Kind = "all" }, fields).Single().Path.ShouldBe("rule.children");
    }

    private static ConditionalRule Compare(string field, string op, params string[] operands) =>
        new() { Kind = "comparison", Field = field, Operator = op,
            Operands = operands.Select(operand => new ConditionalOperand { Literal = JsonSerializer.SerializeToElement(operand) }).ToList() };

    private static ConditionalField Field(string name, string kind) => new() { Name = name, Kind = kind };

    private static Dictionary<string, JsonElement> Values(object input) =>
        JsonSerializer.SerializeToElement(input).EnumerateObject().ToDictionary(entry => entry.Name, entry => entry.Value);
}
