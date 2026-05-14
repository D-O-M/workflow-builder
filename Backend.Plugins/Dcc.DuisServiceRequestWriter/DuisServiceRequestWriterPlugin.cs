using Backend.Plugins.Harness;
using Dcc.Common;

namespace Dcc.DuisServiceRequestWriter;

public sealed class DuisServiceRequestWriterPlugin : IPlugin
{
    private readonly IDccXmlBlobClient? _blobClient;

    public DuisServiceRequestWriterPlugin()
    {
    }

    public DuisServiceRequestWriterPlugin(IDccXmlBlobClient blobClient)
    {
        _blobClient = blobClient;
    }

    public required DuisServiceRequestWriterInput Input { get; init; }
    public DuisServiceRequestWriterResult Result { get; set; } = default!;

    public async Task Execute()
    {
        var requestId = DccIdentifiers.ResolveRequestId(Input.RequestId);
        var commandVariant = DccIdentifiers.Normalize(Input.CommandVariant, DccXmlConstants.DefaultCommandVariant);
        var serviceReference = DccIdentifiers.Normalize(Input.ServiceReference, DccXmlConstants.DefaultServiceReference);
        var serviceReferenceVariant = DccIdentifiers.Normalize(Input.ServiceReferenceVariant, DccXmlConstants.DefaultServiceReferenceVariant);
        var xmlSchemaVersion = DccIdentifiers.Normalize(Input.XmlSchemaVersion, DccXmlConstants.DefaultXmlSchemaVersion);
        var correlationKey = DccCorrelation.BuildKey(requestId, serviceReference, serviceReferenceVariant, commandVariant);

        var xml = DuisServiceRequestXmlBuilder.CreateXml(new DuisServiceRequestXmlOptions
        {
            RequestId = requestId,
            FirstInSequence = Input.FirstInSequence,
            PrecedingServiceRequestId = Input.PrecedingServiceRequestId,
            CommandVariant = commandVariant,
            ServiceReference = serviceReference,
            ServiceReferenceVariant = serviceReferenceVariant,
            XmlSchemaVersion = xmlSchemaVersion,
            BodyElementName = Input.BodyElementName,
            DeviceId = Input.DeviceId,
            Mpxn = Input.Mpxn,
            Uprn = Input.Uprn,
            PostCode = Input.PostCode,
            AddressIdentifier = Input.AddressIdentifier,
            GbcsPayloadBase64 = Input.GbcsPayloadBase64,
            BodyXmlFragment = Input.BodyXmlFragment
        });

        var validation = DccXmlValidator.Validate(xml, Input.XsdPathList);
        DccXmlValidator.ThrowIfInvalid(validation);

        var client = _blobClient ?? DccXmlBlobClient.Create(Input.XmlContainerName);
        var blobName = DccIdentifiers.Normalize(
            Input.XmlBlobName,
            DccXmlBlobNames.Request(correlationKey, requestId));

        await client.UploadTextAsync(blobName, xml);

        Result = new DuisServiceRequestWriterResult
        {
            RequestId = requestId,
            ServiceReference = serviceReference,
            ServiceReferenceVariant = serviceReferenceVariant,
            CommandVariant = commandVariant,
            XmlSchemaVersion = xmlSchemaVersion,
            XmlContainerName = client.ContainerName,
            XmlBlobName = blobName,
            CorrelationKey = correlationKey,
            ValidationStatus = validation.Status,
            ValidationErrors = validation.Errors,
            WrittenAt = DateTimeOffset.UtcNow.ToString("O")
        };
    }
}
