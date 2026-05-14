namespace Dcc.ParseRequestWriter;

public sealed class ParseRequestWriterInput
{
    public string RequestId { get; set; } = "";
    public string CommandVariant { get; set; } = "4";
    public string ServiceReference { get; set; } = "4.8";
    public string ServiceReferenceVariant { get; set; } = "4.8.1";
    public string XmlSchemaVersion { get; set; } = "5.4";
    public string PayloadFormat { get; set; } = "GBCS";
    public string GbcsPayloadBase64 { get; set; } = "";
    public string XmlContainerName { get; set; } = "";
    public string XmlBlobName { get; set; } = "";
    public string XsdPathList { get; set; } = "";
}

public sealed class ParseRequestWriterResult
{
    public string RequestId { get; set; } = "";
    public string ServiceReference { get; set; } = "";
    public string ServiceReferenceVariant { get; set; } = "";
    public string CommandVariant { get; set; } = "";
    public string XmlSchemaVersion { get; set; } = "";
    public string XmlContainerName { get; set; } = "";
    public string XmlBlobName { get; set; } = "";
    public string CorrelationKey { get; set; } = "";
    public string ValidationStatus { get; set; } = "";
    public string ValidationErrors { get; set; } = "";
    public string WrittenAt { get; set; } = "";
}
