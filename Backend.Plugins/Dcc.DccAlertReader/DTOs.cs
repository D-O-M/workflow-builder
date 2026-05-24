namespace Dcc.DccAlertReader;

public sealed class DccAlertReaderInput
{
    public string XmlContainerName { get; set; } = "";
    public string XmlBlobName { get; set; } = "";
    public string ExpectedRequestId { get; set; } = "";
    public string ExpectedCommandVariant { get; set; } = "";
    public string XsdPathList { get; set; } = "";
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class DccAlertReaderResult
{
    public string RequestId { get; set; } = "";
    public string ResponseId { get; set; } = "";
    public string ResponseCode { get; set; } = "";
    public string ResponseDateTime { get; set; } = "";
    public string CommandVariant { get; set; } = "";
    public string MessageType { get; set; } = "";
    public string AlertCode { get; set; } = "";
    public string AlertType { get; set; } = "";
    public string AlertText { get; set; } = "";
    public string ThrottledAlertSequenceId { get; set; } = "";
    public string ThrottledAlertCount { get; set; } = "";
    public string RawXmlBlobName { get; set; } = "";
    public string XmlContainerName { get; set; } = "";
    public string ParsedAt { get; set; } = "";
    public string ValidationStatus { get; set; } = "";
    public string ValidationErrors { get; set; } = "";
    public string CorrelationKey { get; set; } = "";
}
