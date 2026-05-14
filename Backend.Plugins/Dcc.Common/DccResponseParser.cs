using System.Xml.Linq;

namespace Dcc.Common;

public sealed class DccResponseParseResult
{
    public string MessageType { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string ResponseId { get; set; } = "";
    public string ResponseCode { get; set; } = "";
    public string ResponseDateTime { get; set; } = "";
    public string ServiceReference { get; set; } = "";
    public string ServiceReferenceVariant { get; set; } = "";
    public string AlertCode { get; set; } = "";
    public string AlertType { get; set; } = "";
    public string ThrottledAlertSequenceId { get; set; } = "";
    public string ThrottledAlertCount { get; set; } = "";
    public string PayloadElementName { get; set; } = "";
    public string PayloadText { get; set; } = "";
    public string GbcsPayloadBase64 { get; set; } = "";
}

public static class DccResponseParser
{
    public static DccResponseParseResult Parse(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException("DCC XML document has no root element.");
        return root.Name.LocalName switch
        {
            "Response" => ParseResponse(root),
            "ResponseMessage" => ParseResponseMessage(root, new DccResponseParseResult()),
            "DeviceAlertMessage" => ParseDeviceAlert(root, new DccResponseParseResult()),
            "DCCAlertMessage" => ParseDccAlert(root, new DccResponseParseResult()),
            "SMETS1ResponseMessage" => ParseSmets1Response(root, new DccResponseParseResult()),
            "ParseOutput" => ParseParseOutput(root),
            _ => throw new InvalidDataException($"Unsupported DCC XML root element '{root.Name.LocalName}'.")
        };
    }

    public static void EnsureMatches(
        DccResponseParseResult result,
        string? expectedRequestId,
        string? expectedServiceReference,
        string? expectedServiceReferenceVariant)
    {
        EnsureFieldMatches("RequestID", expectedRequestId, result.RequestId);
        EnsureFieldMatches("ServiceReference", expectedServiceReference, result.ServiceReference);
        EnsureFieldMatches("ServiceReferenceVariant", expectedServiceReferenceVariant, result.ServiceReferenceVariant);
    }

    private static DccResponseParseResult ParseResponse(XElement root)
    {
        var result = new DccResponseParseResult();
        var header = Child(root, "Header");
        if (header is not null)
        {
            result.RequestId = ChildValue(header, "RequestID");
            result.ResponseId = ChildValue(header, "ResponseID");
            result.ResponseCode = ChildValue(header, "ResponseCode");
            result.ResponseDateTime = ChildValue(header, "ResponseDateTime");
        }

        var message = Child(root, "Body")?.Elements().FirstOrDefault();
        if (message is null)
        {
            return result;
        }

        return message.Name.LocalName switch
        {
            "ResponseMessage" => ParseResponseMessage(message, result),
            "DeviceAlertMessage" => ParseDeviceAlert(message, result),
            "DCCAlertMessage" => ParseDccAlert(message, result),
            "SMETS1ResponseMessage" => ParseSmets1Response(message, result),
            _ => ParsePayload(message, result, message.Name.LocalName)
        };
    }

    private static DccResponseParseResult ParseResponseMessage(XElement message, DccResponseParseResult result)
    {
        result.MessageType = "ResponseMessage";
        result.ServiceReference = ChildValue(message, "ServiceReference");
        result.ServiceReferenceVariant = ChildValue(message, "ServiceReferenceVariant");
        var payload = message.Elements()
            .FirstOrDefault(element => element.Name.LocalName is not "ServiceReference" and not "ServiceReferenceVariant");
        return payload is null ? result : ParsePayload(payload, result, payload.Name.LocalName);
    }

    private static DccResponseParseResult ParseSmets1Response(XElement message, DccResponseParseResult result)
    {
        result.MessageType = "SMETS1ResponseMessage";
        result.ServiceReference = ChildValue(message, "ServiceReference");
        result.ServiceReferenceVariant = ChildValue(message, "ServiceReferenceVariant");
        result.PayloadElementName = "SMETS1SignedResponse";
        result.PayloadText = ChildValue(message, "SMETS1SignedResponse");
        return result;
    }

    private static DccResponseParseResult ParseDeviceAlert(XElement message, DccResponseParseResult result)
    {
        result.MessageType = "DeviceAlertMessage";
        result.AlertCode = ChildValue(message, "AlertCode");
        result.ThrottledAlertSequenceId = ChildValue(message, "ThrottledAlertSequenceId");
        result.ThrottledAlertCount = ChildValue(message, "ThrottledAlertCount");
        result.GbcsPayloadBase64 = ChildValue(message, "GBCSPayload");
        result.PayloadElementName = "GBCSPayload";
        result.PayloadText = result.GbcsPayloadBase64;
        return result;
    }

    private static DccResponseParseResult ParseDccAlert(XElement message, DccResponseParseResult result)
    {
        result.MessageType = "DCCAlertMessage";
        result.AlertCode = ChildValue(message, "DCCAlertCode");
        result.ThrottledAlertSequenceId = ChildValue(message, "ThrottledAlertSequenceId");
        result.ThrottledAlertCount = ChildValue(message, "ThrottledAlertCount");
        var alert = Child(message, "DCCAlert")?.Elements().FirstOrDefault();
        result.AlertType = alert?.Name.LocalName ?? "";
        result.PayloadElementName = result.AlertType;
        result.PayloadText = alert?.Value.Trim() ?? "";
        return result;
    }

    private static DccResponseParseResult ParseParseOutput(XElement root)
    {
        var payload = root.Elements().FirstOrDefault();
        return new DccResponseParseResult
        {
            MessageType = "ParseOutput",
            PayloadElementName = payload?.Name.LocalName ?? "",
            PayloadText = payload?.Value.Trim() ?? ""
        };
    }

    private static DccResponseParseResult ParsePayload(
        XElement payload,
        DccResponseParseResult result,
        string payloadName)
    {
        result.PayloadElementName = payloadName;
        result.PayloadText = payload.Value.Trim();
        result.GbcsPayloadBase64 = DescendantValue(payload, "GBCSPayload");
        return result;
    }

    private static XElement? Child(XElement element, string localName) =>
        element.Elements().FirstOrDefault(child => child.Name.LocalName == localName);

    private static string ChildValue(XElement element, string localName) =>
        Child(element, localName)?.Value.Trim() ?? "";

    private static string DescendantValue(XElement element, string localName) =>
        element.Descendants().FirstOrDefault(child => child.Name.LocalName == localName)?.Value.Trim() ?? "";

    private static void EnsureFieldMatches(string fieldName, string? expected, string actual)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return;
        }

        if (!string.Equals(expected.Trim(), actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"DCC correlation mismatch for {fieldName}: expected '{expected.Trim()}', received '{actual}'.");
        }
    }
}
