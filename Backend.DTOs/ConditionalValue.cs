using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Backend.Models;

internal static class ConditionalValue
{
    public static bool TryRead(JsonElement value, ConditionalField field, bool literal, out IComparable? parsed)
    {
        parsed = null;
        if (field.Kind is "Integer" or "Decimal")
        {
            var numberText = value.ValueKind switch
            {
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.String when literal => value.GetString(),
                _ => null
            };
            if (!ConditionalNumber.TryParse(numberText, field.Kind == "Integer", out var number))
                return false;
            parsed = number;
            return true;
        }
        if (field.Kind == "Boolean" && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            parsed = value.GetBoolean();
            return true;
        }
        if (value.ValueKind != JsonValueKind.String)
            return false;
        var text = value.GetString()!;
        switch (field.Kind)
        {
            case "String":
                parsed = text;
                return true;
            case "Enum" when field.EnumValues?.Contains(text, StringComparer.Ordinal) == true:
                parsed = text;
                return true;
            case "Guid" when Guid.TryParse(text, out var guid):
                parsed = guid;
                return true;
            case "Date" when DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date):
                parsed = date;
                return true;
            case "DateTime" when text.Length > 10 && text[10] == 'T' && DateTimeOffset.TryParseExact(text,
                ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mmK"], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant):
                parsed = instant;
                return true;
            default:
                return false;
        }
    }
}

// Compare JSON decimal representations exactly, including signed/unsigned integers, decimal
// and finite scientific-notation values emitted by floating-point Plugin properties.
internal sealed partial record ConditionalNumber(BigInteger Coefficient, int Scale) : IComparable
{
    [GeneratedRegex(@"\A([+-]?)([0-9]*)(?:\.([0-9]*))?(?:[eE]([+-]?[0-9]+))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    public static ConditionalNumber FromCount(int count) => new(count, 0);

    public static bool TryParse(string? text, bool integer, out ConditionalNumber? value)
    {
        value = null;
        if (text is null || text.Length > 1024)
            return false;
        var match = NumberPattern().Match(text);
        if (!match.Success || match.Groups[2].Length + match.Groups[3].Length == 0)
            return false;
        var exponent = 0;
        if (match.Groups[4].Success && (!int.TryParse(match.Groups[4].Value, NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out exponent) || exponent is < -1024 or > 1024))
            return false;
        var fraction = match.Groups[3].Value;
        var scale = fraction.Length - exponent;
        var coefficient = BigInteger.Parse(match.Groups[1].Value + match.Groups[2].Value + fraction, CultureInfo.InvariantCulture);
        if (coefficient.IsZero)
            scale = 0;
        else
        {
            // Keep equivalent representations (e.g. 1, 1.00, 10e-1) hash-equal for collection membership.
            while (coefficient % 10 == 0)
            {
                coefficient /= 10;
                scale--;
            }
        }
        if (integer && scale > 0)
            return false;
        value = new ConditionalNumber(coefficient, scale);
        return true;
    }

    public int CompareTo(object? obj)
    {
        var other = (ConditionalNumber)obj!;
        var delta = Scale - other.Scale;
        if (delta > 0)
            return Coefficient.CompareTo(other.Coefficient * BigInteger.Pow(10, delta));
        return delta < 0
            ? (Coefficient * BigInteger.Pow(10, -delta)).CompareTo(other.Coefficient)
            : Coefficient.CompareTo(other.Coefficient);
    }
}
