namespace Dcc.DuisResponseReader;

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class DuisResponseReaderInput
{
    public string XmlContainerName { get; set; } = "";
    public string XmlBlobName { get; set; } = "";
    public string ExpectedRequestId { get; set; } = "";
    public string ExpectedServiceReference { get; set; } = "";
    public string ExpectedServiceReferenceVariant { get; set; } = "";
    public string ExpectedCommandVariant { get; set; } = "";
    public string XsdPathList { get; set; } = "";
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class DuisResponseReaderResult
{
    public string RequestId { get; set; } = "";
    public string ResponseId { get; set; } = "";
    public string ResponseCode { get; set; } = "";
    public string ResponseDateTime { get; set; } = "";
    public string ServiceReference { get; set; } = "";
    public string ServiceReferenceVariant { get; set; } = "";
    public string CommandVariant { get; set; } = "";
    public string MessageType { get; set; } = "";
    public string PayloadElementName { get; set; } = "";
    public string PayloadText { get; set; } = "";
    public string GbcsPayloadBase64 { get; set; } = "";
    public string RawXmlBlobName { get; set; } = "";
    public string XmlContainerName { get; set; } = "";
    public string ParsedAt { get; set; } = "";
    public string ValidationStatus { get; set; } = "";
    public string ValidationErrors { get; set; } = "";
    public string CorrelationKey { get; set; } = "";
}
