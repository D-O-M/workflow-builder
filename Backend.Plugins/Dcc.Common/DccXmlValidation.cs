using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace Dcc.Common;

public sealed class DccXmlValidationReport
{
    public required string Status { get; init; }
    public required string Errors { get; init; }
}

public static class DccXmlValidator
{
    public static DccXmlValidationReport Validate(string xml, string? xsdPathList)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException)
        {
            return new DccXmlValidationReport
            {
                Status = "Invalid",
                Errors = ex.Message
            };
        }

        var xsdPaths = SplitPathList(xsdPathList);
        if (xsdPaths.Length == 0)
        {
            return new DccXmlValidationReport
            {
                Status = "WellFormed",
                Errors = ""
            };
        }

        var schemaSet = new XmlSchemaSet();
        foreach (var path in xsdPaths)
        {
            schemaSet.Add(null, path);
        }

        var validationErrors = new List<string>();
        document.Validate(schemaSet, (_, args) => validationErrors.Add(args.Message));
        return validationErrors.Count == 0
            ? new DccXmlValidationReport { Status = "Valid", Errors = "" }
            : new DccXmlValidationReport { Status = "Invalid", Errors = string.Join("; ", validationErrors) };
    }

    public static void ThrowIfInvalid(DccXmlValidationReport report)
    {
        if (string.Equals(report.Status, "Invalid", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"DCC XML validation failed: {report.Errors}");
        }
    }

    private static string[] SplitPathList(string? pathList) =>
        string.IsNullOrWhiteSpace(pathList)
            ? []
            : pathList.Split([';', '|', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
