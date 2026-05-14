using System.Xml.Linq;

namespace Dcc.Common;

public sealed class DuisParseRequestXmlOptions
{
    public string RequestId { get; init; } = "";
    public string CommandVariant { get; init; } = "4";
    public string ServiceReference { get; init; } = DccXmlConstants.DefaultServiceReference;
    public string ServiceReferenceVariant { get; init; } = DccXmlConstants.DefaultServiceReferenceVariant;
    public string XmlSchemaVersion { get; init; } = DccXmlConstants.DefaultXmlSchemaVersion;
    public string PayloadFormat { get; init; } = "GBCS";
    public string GbcsPayloadBase64 { get; init; } = "";
}

public static class DuisParseRequestXmlBuilder
{
    public static string CreateXml(DuisParseRequestXmlOptions options)
    {
        XNamespace sr = DccXmlConstants.ServiceUserGatewayNamespace;
        var requestId = DccIdentifiers.ResolveRequestId(options.RequestId);
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                sr + "ParseRequest",
                new XAttribute(XNamespace.Xmlns + "sr", sr),
                new XAttribute("schemaVersion", DccIdentifiers.Normalize(options.XmlSchemaVersion, DccXmlConstants.DefaultXmlSchemaVersion)),
                new XElement(
                    sr + "Header",
                    new XElement(sr + "RequestID", requestId),
                    new XElement(sr + "CommandVariant", DccIdentifiers.Normalize(options.CommandVariant, "4")),
                    new XElement(sr + "ServiceReference", DccIdentifiers.Normalize(options.ServiceReference, DccXmlConstants.DefaultServiceReference)),
                    new XElement(sr + "ServiceReferenceVariant", DccIdentifiers.Normalize(options.ServiceReferenceVariant, DccXmlConstants.DefaultServiceReferenceVariant))),
                new XElement(
                    sr + "Body",
                    new XElement(sr + "PayloadFormat", DccIdentifiers.Normalize(options.PayloadFormat, "GBCS")),
                    new XElement(sr + "GBCSPayload", options.GbcsPayloadBase64.Trim()))));

        return document.ToString(SaveOptions.DisableFormatting);
    }
}
