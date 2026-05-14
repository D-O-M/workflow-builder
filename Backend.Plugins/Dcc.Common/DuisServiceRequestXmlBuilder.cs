using System.Xml.Linq;

namespace Dcc.Common;

public sealed class DuisServiceRequestXmlOptions
{
    public string RequestId { get; init; } = "";
    public bool FirstInSequence { get; init; }
    public string PrecedingServiceRequestId { get; init; } = "";
    public string CommandVariant { get; init; } = DccXmlConstants.DefaultCommandVariant;
    public string ServiceReference { get; init; } = DccXmlConstants.DefaultServiceReference;
    public string ServiceReferenceVariant { get; init; } = DccXmlConstants.DefaultServiceReferenceVariant;
    public string XmlSchemaVersion { get; init; } = DccXmlConstants.DefaultXmlSchemaVersion;
    public string BodyElementName { get; init; } = "ReadInventory";
    public string DeviceId { get; init; } = "";
    public string Mpxn { get; init; } = "";
    public string Uprn { get; init; } = "";
    public string PostCode { get; init; } = "";
    public string AddressIdentifier { get; init; } = "";
    public string GbcsPayloadBase64 { get; init; } = "";
    public string BodyXmlFragment { get; init; } = "";
}

public static class DuisServiceRequestXmlBuilder
{
    public static string CreateXml(DuisServiceRequestXmlOptions options)
    {
        XNamespace sr = DccXmlConstants.ServiceUserGatewayNamespace;
        var requestId = DccIdentifiers.ResolveRequestId(options.RequestId);
        var body = BuildBody(options, sr);
        var header = new XElement(
            sr + "Header",
            new XElement(sr + "RequestID", requestId));

        if (options.FirstInSequence)
        {
            header.Add(new XElement(sr + "FirstInSequence", "true"));
        }

        if (!string.IsNullOrWhiteSpace(options.PrecedingServiceRequestId))
        {
            header.Add(new XElement(sr + "PrecedingServiceRequestID", options.PrecedingServiceRequestId.Trim()));
        }

        header.Add(
            new XElement(sr + "CommandVariant", DccIdentifiers.Normalize(options.CommandVariant, DccXmlConstants.DefaultCommandVariant)),
            new XElement(sr + "ServiceReference", DccIdentifiers.Normalize(options.ServiceReference, DccXmlConstants.DefaultServiceReference)),
            new XElement(sr + "ServiceReferenceVariant", DccIdentifiers.Normalize(options.ServiceReferenceVariant, DccXmlConstants.DefaultServiceReferenceVariant)));

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                sr + "Request",
                new XAttribute(XNamespace.Xmlns + "sr", sr),
                new XAttribute("schemaVersion", DccIdentifiers.Normalize(options.XmlSchemaVersion, DccXmlConstants.DefaultXmlSchemaVersion)),
                header,
                new XElement(sr + "Body", body)));

        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement BuildBody(DuisServiceRequestXmlOptions options, XNamespace sr)
    {
        if (!string.IsNullOrWhiteSpace(options.BodyXmlFragment))
        {
            return XElement.Parse(options.BodyXmlFragment, LoadOptions.PreserveWhitespace);
        }

        var bodyElementName = NormalizeElementName(options.BodyElementName, "ReadInventory");
        var body = new XElement(sr + bodyElementName);

        if (!string.IsNullOrWhiteSpace(options.DeviceId))
        {
            body.Add(new XElement(sr + "DeviceID", options.DeviceId.Trim()));
        }
        else if (!string.IsNullOrWhiteSpace(options.Mpxn))
        {
            body.Add(new XElement(sr + "MPxN", options.Mpxn.Trim()));
        }
        else if (!string.IsNullOrWhiteSpace(options.Uprn))
        {
            body.Add(new XElement(sr + "UPRN", options.Uprn.Trim()));
        }
        else if (!string.IsNullOrWhiteSpace(options.PostCode) || !string.IsNullOrWhiteSpace(options.AddressIdentifier))
        {
            body.Add(
                new XElement(
                    sr + "PropertyFilter",
                    new XElement(sr + "PostCode", options.PostCode.Trim()),
                    new XElement(sr + "AddressIdentifier", options.AddressIdentifier.Trim())));
        }

        if (!string.IsNullOrWhiteSpace(options.GbcsPayloadBase64))
        {
            body.Add(new XElement(sr + "GBCSPayload", options.GbcsPayloadBase64.Trim()));
        }

        return body;
    }

    private static string NormalizeElementName(string elementName, string fallback)
    {
        var value = DccIdentifiers.Normalize(elementName, fallback);
        if (value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not '.'))
        {
            throw new InvalidDataException($"'{value}' is not a valid simple XML element name.");
        }

        return value;
    }
}
