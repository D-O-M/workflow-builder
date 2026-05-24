namespace Dcc.GatewaySubmit;

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class GatewaySubmitInput
{
    public string XmlContainerName { get; set; } = "";
    public string XmlBlobName { get; set; } = "";
    public string ResponseXmlBlobName { get; set; } = "";
    public string EndpointUrl { get; set; } = "";
    public string HttpMethod { get; set; } = "";
    public int TimeoutSeconds { get; set; }
    public string MockResponseXml { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string ServiceReference { get; set; } = "";
    public string ServiceReferenceVariant { get; set; } = "";
    public string CommandVariant { get; set; } = "";
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class GatewaySubmitResult
{
    public string RequestId { get; set; } = "";
    public string ServiceReference { get; set; } = "";
    public string ServiceReferenceVariant { get; set; } = "";
    public string CommandVariant { get; set; } = "";
    public string RequestXmlBlobName { get; set; } = "";
    public string ResponseXmlBlobName { get; set; } = "";
    public string XmlContainerName { get; set; } = "";
    public string CorrelationKey { get; set; } = "";
    public bool Succeeded { get; set; }
    public int StatusCode { get; set; }
    public string TransportStatus { get; set; } = "";
    public int ResponseLength { get; set; }
    public string SubmittedAt { get; set; } = "";
}
