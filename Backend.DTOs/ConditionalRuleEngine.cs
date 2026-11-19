using System.Text.Json;

namespace Backend.Models;

public static class ConditionalRuleEngine
{
    private const int MaxDepth = 8;
    private const int MaxNodes = 128;
    private static readonly IReadOnlyDictionary<(string Kind, string Operator), OperatorDefinition> Operators = LoadOperators();

    public static IReadOnlyList<ConditionalRuleError> Validate(ConditionalRule? rule, IReadOnlyCollection<ConditionalField> fields)
    {
        var schema = IndexFields(fields);
        var errors = new List<ConditionalRuleError>();
        var visited = 0;
        ValidateNode(rule, schema, "rule", 1, ref visited, errors);
        return errors;
    }

    public static bool Evaluate(ConditionalRule? rule, IReadOnlyCollection<ConditionalField> fields,
        IReadOnlyDictionary<string, JsonElement> values)
    {
        var schema = IndexFields(fields);
        var errors = new List<ConditionalRuleError>();
        var visited = 0;
        ValidateNode(rule, schema, "rule", 1, ref visited, errors);
        if (errors.Count > 0)
            throw new InvalidDataException($"{errors[0].Path}: {errors[0].Message}");
        return EvaluateNode(rule!, schema, values, "rule");
    }

    private static Dictionary<string, ConditionalField> IndexFields(IReadOnlyCollection<ConditionalField> fields)
    {
        var schema = new Dictionary<string, ConditionalField>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (field is null || string.IsNullOrWhiteSpace(field.Name) || !schema.TryAdd(field.Name, field))
                throw new InvalidDataException("Conditional input schema contains an unnamed or duplicate field.");
        }
        return schema;
    }

    private static void ValidateNode(ConditionalRule? rule, IReadOnlyDictionary<string, ConditionalField> fields,
        string path, int depth, ref int visited, List<ConditionalRuleError> errors)
    {
        if (++visited > MaxNodes || depth > MaxDepth)
        {
            errors.Add(new(path, $"Use at most {MaxNodes} rules and groups and {MaxDepth} levels."));
            return;
        }
        if (rule is null)
        {
            errors.Add(new(path, "A rule is required."));
            return;
        }
        if (rule.Kind is "all" or "any")
        {
            if (rule.Children is not { Count: > 0 })
                errors.Add(new(path + ".children", "Add a rule to this group."));
            if (rule.Field is not null || rule.Operator is not null || rule.IgnoreCase || rule.Operands?.Count > 0)
                errors.Add(new(path, "A group may contain only child rules and negation."));
            for (var i = 0; i < (rule.Children?.Count ?? 0) && visited <= MaxNodes; i++)
                ValidateNode(rule.Children![i], fields, $"{path}.children[{i}]", depth + 1, ref visited, errors);
            return;
        }
        if (rule.Kind != "comparison")
        {
            errors.Add(new(path + ".kind", "Choose a comparison, All or Any."));
            return;
        }
        if (rule.Children?.Count > 0)
            errors.Add(new(path + ".children", "A comparison cannot contain child rules."));
        if (rule.Field is null || !fields.TryGetValue(rule.Field, out var field))
        {
            errors.Add(new(path + ".field", "Choose a field declared by the connected output."));
            return;
        }
        if (!WorkflowPropertyTypeName.TryParse(field.Kind, out _, out var collection) || rule.Operator is null ||
            !Operators.TryGetValue((collection ? "Collection" : field.Kind, rule.Operator), out var op))
        {
            errors.Add(new(path + ".operator", $"Choose a valid comparison for '{field.Name}' ({field.Kind})."));
            return;
        }
        if (rule.IgnoreCase && !op.IgnoreCase)
            errors.Add(new(path + ".ignoreCase", "Case sensitivity applies only to text comparisons."));
        var count = rule.Operands?.Count ?? 0;
        if (count < op.MinOperands || count > op.MaxOperands)
        {
            errors.Add(new(path + ".operands", $"'{op.Id}' requires {op.MinOperands} to {op.MaxOperands} comparison values."));
            return;
        }
        var expected = OperandField(field, op);
        for (var i = 0; i < count; i++)
        {
            var operand = rule.Operands![i];
            var operandPath = $"{path}.operands[{i}]";
            if (operand is null || (operand.Field is null) == (operand.Literal is null))
            {
                errors.Add(new(operandPath, "Choose one field or one literal."));
                continue;
            }
            if (operand.Field is { } reference)
            {
                if (!fields.TryGetValue(reference, out var other) || !AreCompatible(expected, other, op.AcceptsCollectionField))
                    errors.Add(new(operandPath + ".field", $"Choose a compatible {expected.Kind} field."));
            }
            else if (!ConditionalValue.TryRead(operand.Literal!.Value, expected, literal: true, out _))
                errors.Add(new(operandPath + ".literal", $"Enter a valid {expected.Kind} value for '{field.Name}'."));
        }
    }

    private static ConditionalField OperandField(ConditionalField field, OperatorDefinition op) => op.OperandKind switch
    {
        "item" => field with { Kind = WorkflowPropertyTypeName.ToScalarKindName(field.Kind) },
        "count" => field with { Kind = "Integer" },
        _ => field
    };

    private static bool AreCompatible(ConditionalField expected, ConditionalField other, bool acceptsCollection)
    {
        if (acceptsCollection && other.Kind.EndsWith("[]", StringComparison.Ordinal))
            other = other with { Kind = WorkflowPropertyTypeName.ToScalarKindName(other.Kind) };
        if (expected.Kind != other.Kind)
            return expected.Kind is ("Integer" or "Decimal") && other.Kind is ("Integer" or "Decimal");
        return expected.Kind != "Enum" || (expected.EnumValues ?? []).ToHashSet(StringComparer.Ordinal).SetEquals(other.EnumValues ?? []);
    }

    private static bool EvaluateNode(ConditionalRule rule, IReadOnlyDictionary<string, ConditionalField> fields,
        IReadOnlyDictionary<string, JsonElement> values, string path)
    {
        bool result;
        if (rule.Kind == "comparison")
            result = EvaluateComparison(rule, fields, values, path);
        else
        {
            result = rule.Kind == "all";
            for (var i = 0; i < rule.Children.Count; i++)
            {
                var child = EvaluateNode(rule.Children[i], fields, values, $"{path}.children[{i}]");
                if (child != result)
                {
                    result = child;
                    break;
                }
            }
        }
        return rule.Negated ? !result : result;
    }

    private static bool EvaluateComparison(ConditionalRule rule, IReadOnlyDictionary<string, ConditionalField> fields,
        IReadOnlyDictionary<string, JsonElement> values, string path)
    {
        var field = fields[rule.Field!];
        var present = values.TryGetValue(field.Name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
        if (!present)
        {
            return rule.Operator switch
            {
                "hasValue" => false,
                "hasNoValue" => true,
                _ => throw Missing(field, path)
            };
        }

        var isCollection = field.Kind.EndsWith("[]", StringComparison.Ordinal);
        var definition = Operators[(isCollection ? "Collection" : field.Kind, rule.Operator!)];
        var operandType = OperandField(field, definition);
        var operands = new List<IComparable>();
        for (var i = 0; i < (rule.Operands?.Count ?? 0); i++)
        {
            var operand = rule.Operands![i];
            var operandPath = $"{path}.operands[{i}]";
            if (operand.Field is { } reference)
            {
                var other = fields[reference];
                if (!values.TryGetValue(reference, out var otherValue) ||
                    otherValue.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    throw Missing(other, operandPath);
                if (definition.AcceptsCollectionField && other.Kind.EndsWith("[]", StringComparison.Ordinal))
                    operands.AddRange(ReadCollection(otherValue, other, operandPath));
                else
                    operands.Add(Read(otherValue, other, operandPath));
            }
            else
            {
                ConditionalValue.TryRead(operand.Literal!.Value, operandType, literal: true, out var parsed);
                operands.Add(parsed!);
            }
        }
        if (isCollection)
            return EvaluateCollection(rule.Operator!, value, field, operands, path);

        var source = Read(value, field, path);
        var comparison = rule.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return rule.Operator switch
        {
            "hasValue" => true,
            "hasNoValue" => false,
            "isTrue" => (bool)source,
            "isFalse" => !(bool)source,
            "isEmpty" => ((string)source).Length == 0,
            "isBlank" => string.IsNullOrWhiteSpace((string)source),
            "startsWith" => ((string)source).StartsWith((string)operands[0], comparison),
            "endsWith" => ((string)source).EndsWith((string)operands[0], comparison),
            "contains" => ((string)source).Contains((string)operands[0], comparison),
            "isOneOf" => operands.Any(item => Compare(source, item, comparison) == 0),
            "between" => Compare(source, operands[0], comparison) >= 0 && Compare(source, operands[1], comparison) <= 0,
            _ => CompareOrder(rule.Operator!, Compare(source, operands[0], comparison))
        };
    }

    private static bool EvaluateCollection(string op, JsonElement value, ConditionalField field, List<IComparable> operands, string path)
    {
        var candidates = op is "contains" or "containsAny" or "containsAll" ? operands.ToHashSet() : null;
        var foundAny = false;
        foreach (var item in ReadCollection(value, field, path))
            foundAny |= candidates?.Remove(item) == true;
        return op switch
        {
            "hasValue" => true,
            "hasNoValue" => false,
            "isEmpty" => value.GetArrayLength() == 0,
            "contains" or "containsAny" => foundAny,
            "containsAll" => candidates!.Count == 0,
            _ => CompareOrder(op, ConditionalNumber.FromCount(value.GetArrayLength()).CompareTo(operands[0]))
        };
    }

    private static IEnumerable<IComparable> ReadCollection(JsonElement value, ConditionalField field, string path)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw Malformed(field, path);
        var elementType = field with { Kind = WorkflowPropertyTypeName.ToScalarKindName(field.Kind) };
        foreach (var element in value.EnumerateArray())
            yield return Read(element, elementType, path);
    }

    private static IComparable Read(JsonElement value, ConditionalField field, string path) =>
        ConditionalValue.TryRead(value, field, literal: false, out var parsed) ? parsed! : throw Malformed(field, path);

    private static InvalidDataException Malformed(ConditionalField field, string path) =>
        new($"{path}: Field '{field.Name}' must contain a valid {field.Kind} value.");

    private static InvalidDataException Missing(ConditionalField field, string path) =>
        new($"{path}: Field '{field.Name}' ({field.Kind}) is missing or null.");

    private static int Compare(IComparable left, IComparable right, StringComparison comparison) =>
        left is string text ? string.Compare(text, (string)right, comparison) : left.CompareTo(right);

    private static bool CompareOrder(string op, int order) => op switch
    {
        "eq" or "countEq" => order == 0,
        "ne" or "countNe" => order != 0,
        "gt" or "countGt" => order > 0,
        "gte" or "countGte" => order >= 0,
        "lt" or "countLt" => order < 0,
        "lte" or "countLte" => order <= 0,
        _ => throw new InvalidDataException("Unrecognized conditional comparison.")
    };

    private static IReadOnlyDictionary<(string Kind, string Operator), OperatorDefinition> LoadOperators()
    {
        using var stream = typeof(ConditionalRuleEngine).Assembly.GetManifestResourceStream("Backend.Models.ConditionalOperators.json")!;
        var definitions = JsonSerializer.Deserialize<OperatorDefinition[]>(stream, JsonSerializerOptions.Web)!;
        return definitions.SelectMany(op => op.Kinds.Select(kind => (Key: (kind, op.Id), Value: op)))
            .ToDictionary(entry => entry.Key, entry => entry.Value);
    }

    private sealed record OperatorDefinition(string Id, string[] Kinds, string OperandKind, int MinOperands,
        int MaxOperands, bool IgnoreCase, bool AcceptsCollectionField = false);
}
