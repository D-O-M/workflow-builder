using Backend.Plugins.Harness;
using Dcc.Common;

namespace Dcc.DeviceAlertReader;

public sealed class DeviceAlertReaderPlugin : IPlugin
{
    private readonly IDccXmlBlobClient? _blobClient;

    public DeviceAlertReaderPlugin()
    {
    }

    public DeviceAlertReaderPlugin(IDccXmlBlobClient blobClient)
    {
        _blobClient = blobClient;
    }

    public required DeviceAlertReaderInput Input { get; init; }
    public DeviceAlertReaderResult Result { get; set; } = default!;

    public async Task Execute()
    {
        var client = _blobClient ?? DccXmlBlobClient.Create(Input.XmlContainerName);
        var xml = await client.DownloadTextAsync(Input.XmlBlobName);
        var validation = DccXmlValidator.Validate(xml, Input.XsdPathList);
        DccXmlValidator.ThrowIfInvalid(validation);

        var parsed = DccResponseParser.Parse(xml);
        if (!string.Equals(parsed.MessageType, "DeviceAlertMessage", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Expected DeviceAlertMessage but received '{parsed.MessageType}'.");
        }

        DccResponseParser.EnsureMatches(parsed, Input.ExpectedRequestId, "", "");
        var commandVariant = DccIdentifiers.Normalize(Input.ExpectedCommandVariant, "");
        Result = new DeviceAlertReaderResult
        {
            RequestId = parsed.RequestId,
            ResponseId = parsed.ResponseId,
            ResponseCode = parsed.ResponseCode,
            ResponseDateTime = parsed.ResponseDateTime,
            CommandVariant = commandVariant,
            MessageType = parsed.MessageType,
            AlertCode = parsed.AlertCode,
            ThrottledAlertSequenceId = parsed.ThrottledAlertSequenceId,
            ThrottledAlertCount = parsed.ThrottledAlertCount,
            GbcsPayloadBase64 = parsed.GbcsPayloadBase64,
            RawXmlBlobName = Input.XmlBlobName,
            XmlContainerName = client.ContainerName,
            ParsedAt = DateTimeOffset.UtcNow.ToString("O"),
            ValidationStatus = validation.Status,
            ValidationErrors = validation.Errors,
            CorrelationKey = DccCorrelation.BuildKey(parsed.RequestId, "", "", commandVariant)
        };
    }
}
