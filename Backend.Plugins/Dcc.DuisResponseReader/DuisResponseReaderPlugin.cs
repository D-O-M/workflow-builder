using Backend.Plugins.Harness;
using Microsoft.Extensions.DependencyInjection;
using Dcc.Common;

namespace Dcc.DuisResponseReader;

public sealed class DuisResponseReaderPlugin : IPlugin
{
    private readonly IDccXmlBlobClient? _blobClient;

    [ActivatorUtilitiesConstructor]
    public DuisResponseReaderPlugin()
    {
    }

    public DuisResponseReaderPlugin(IDccXmlBlobClient blobClient)
    {
        _blobClient = blobClient;
    }

    public required DuisResponseReaderInput Input { get; init; }
    public DuisResponseReaderResult Result { get; set; } = default!;

    public async Task Execute()
    {
        var client = _blobClient ?? DccXmlBlobClient.Create(Input.XmlContainerName);
        var xml = await client.DownloadTextAsync(Input.XmlBlobName);
        var validation = DccXmlValidator.Validate(xml, Input.XsdPathList);
        DccXmlValidator.ThrowIfInvalid(validation);

        var parsed = DccResponseParser.Parse(xml);
        if (parsed.MessageType is "DeviceAlertMessage" or "DCCAlertMessage")
        {
            throw new InvalidDataException($"DCC solicited response reader received alert message type '{parsed.MessageType}'.");
        }

        DccResponseParser.EnsureMatches(
            parsed,
            Input.ExpectedRequestId,
            Input.ExpectedServiceReference,
            Input.ExpectedServiceReferenceVariant);

        var commandVariant = DccIdentifiers.Normalize(Input.ExpectedCommandVariant, "");
        var serviceReference = DccIdentifiers.Normalize(parsed.ServiceReference, Input.ExpectedServiceReference);
        var serviceReferenceVariant = DccIdentifiers.Normalize(parsed.ServiceReferenceVariant, Input.ExpectedServiceReferenceVariant);
        Result = new DuisResponseReaderResult
        {
            RequestId = parsed.RequestId,
            ResponseId = parsed.ResponseId,
            ResponseCode = parsed.ResponseCode,
            ResponseDateTime = parsed.ResponseDateTime,
            ServiceReference = serviceReference,
            ServiceReferenceVariant = serviceReferenceVariant,
            CommandVariant = commandVariant,
            MessageType = parsed.MessageType,
            PayloadElementName = parsed.PayloadElementName,
            PayloadText = parsed.PayloadText,
            GbcsPayloadBase64 = parsed.GbcsPayloadBase64,
            RawXmlBlobName = Input.XmlBlobName,
            XmlContainerName = client.ContainerName,
            ParsedAt = DateTimeOffset.UtcNow.ToString("O"),
            ValidationStatus = validation.Status,
            ValidationErrors = validation.Errors,
            CorrelationKey = DccCorrelation.BuildKey(parsed.RequestId, serviceReference, serviceReferenceVariant, commandVariant)
        };
    }
}
