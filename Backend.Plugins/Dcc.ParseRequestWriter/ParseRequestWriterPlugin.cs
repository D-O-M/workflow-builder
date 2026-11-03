using Backend.Plugins.Harness;
using Microsoft.Extensions.DependencyInjection;
using Dcc.Common;

namespace Dcc.ParseRequestWriter;

public sealed class ParseRequestWriterPlugin : IPlugin
{
    private readonly IDccXmlBlobClient? _blobClient;

    [ActivatorUtilitiesConstructor]
    public ParseRequestWriterPlugin()
    {
    }

    public ParseRequestWriterPlugin(IDccXmlBlobClient blobClient)
    {
        _blobClient = blobClient;
    }

    public required ParseRequestWriterInput Input { get; init; }
    public ParseRequestWriterResult Result { get; set; } = default!;

    public async Task Execute()
    {
        var requestId = DccIdentifiers.ResolveRequestId(Input.RequestId);
        var commandVariant = DccIdentifiers.Normalize(Input.CommandVariant, "4");
        var serviceReference = DccIdentifiers.Normalize(Input.ServiceReference, DccXmlConstants.DefaultServiceReference);
        var serviceReferenceVariant = DccIdentifiers.Normalize(Input.ServiceReferenceVariant, DccXmlConstants.DefaultServiceReferenceVariant);
        var xmlSchemaVersion = DccIdentifiers.Normalize(Input.XmlSchemaVersion, DccXmlConstants.DefaultXmlSchemaVersion);
        var correlationKey = DccCorrelation.BuildKey(requestId, serviceReference, serviceReferenceVariant, commandVariant);

        var xml = DuisParseRequestXmlBuilder.CreateXml(new DuisParseRequestXmlOptions
        {
            RequestId = requestId,
            CommandVariant = commandVariant,
            ServiceReference = serviceReference,
            ServiceReferenceVariant = serviceReferenceVariant,
            XmlSchemaVersion = xmlSchemaVersion,
            PayloadFormat = Input.PayloadFormat,
            GbcsPayloadBase64 = Input.GbcsPayloadBase64
        });

        var validation = DccXmlValidator.Validate(xml, Input.XsdPathList);
        DccXmlValidator.ThrowIfInvalid(validation);

        var client = _blobClient ?? DccXmlBlobClient.Create(Input.XmlContainerName);
        var blobName = DccIdentifiers.Normalize(
            Input.XmlBlobName,
            DccXmlBlobNames.Request(correlationKey, requestId));

        await client.UploadTextAsync(blobName, xml);

        Result = new ParseRequestWriterResult
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
