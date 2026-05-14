namespace Dcc.DuisServiceRequestWriter;

public sealed class DuisServiceRequestWriterInput
{
    public string RequestId { get; set; } = "";
    public bool FirstInSequence { get; set; }
    public string PrecedingServiceRequestId { get; set; } = "";
    public string CommandVariant { get; set; } = "1";
    public string ServiceReference { get; set; } = "4.8";
    public string ServiceReferenceVariant { get; set; } = "4.8.1";
    public string XmlSchemaVersion { get; set; } = "5.4";
    public string BodyElementName { get; set; } = "ReadInventory";
    public string DeviceId { get; set; } = "";
    public string Mpxn { get; set; } = "";
    public string Uprn { get; set; } = "";
    public string PostCode { get; set; } = "";
    public string AddressIdentifier { get; set; } = "";
    public string GbcsPayloadBase64 { get; set; } = "";
    public string BodyXmlFragment { get; set; } = "";
    public string XmlContainerName { get; set; } = "";
    public string XmlBlobName { get; set; } = "";
    public string XsdPathList { get; set; } = "";
}

public sealed class DuisServiceRequestWriterResult
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
