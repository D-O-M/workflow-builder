using Backend.Database.Relational.Entities;

namespace Backend.Models;

public static class WorkflowPropertyTypeName
{
    private const string CollectionSuffix = "[]";

    public static string Format(WorkflowPropertyKind kind, bool isCollection = false) =>
        isCollection ? $"{kind}{CollectionSuffix}" : kind.ToString();

    public static bool TryParse(
        string? value,
        out WorkflowPropertyKind kind,
        out bool isCollection)
    {
        kind = default;
        isCollection = false;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.EndsWith(CollectionSuffix, StringComparison.Ordinal))
        {
            isCollection = true;
            normalized = normalized[..^CollectionSuffix.Length];
            if (normalized.EndsWith(CollectionSuffix, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return Enum.TryParse(normalized, ignoreCase: false, out kind) &&
               Enum.IsDefined(typeof(WorkflowPropertyKind), kind);
    }

    public static WorkflowPropertyKind ParseScalar(string value)
    {
        if (!TryParse(value, out var kind, out var isCollection) || isCollection)
        {
            throw new InvalidDataException($"'{value}' is not a supported scalar property type.");
        }

        return kind;
    }

    public static string ToCollectionKindName(string scalarKindName)
    {
        var scalarKind = ParseScalar(scalarKindName);
        return Format(scalarKind, isCollection: true);
    }

    public static string ToScalarKindName(string collectionKindName)
    {
        if (!TryParse(collectionKindName, out var kind, out var isCollection) || !isCollection)
        {
            throw new InvalidDataException($"'{collectionKindName}' is not a supported collection property type.");
        }

        return Format(kind);
    }
}
