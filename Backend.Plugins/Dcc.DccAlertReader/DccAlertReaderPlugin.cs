using Backend.Plugins.Harness;
using Microsoft.Extensions.DependencyInjection;
using Dcc.Common;

namespace Dcc.DccAlertReader;

public sealed class DccAlertReaderPlugin : IPlugin
{
    private readonly IDccXmlBlobClient? _blobClient;

    [ActivatorUtilitiesConstructor]
    public DccAlertReaderPlugin()
    {
    }

    public DccAlertReaderPlugin(IDccXmlBlobClient blobClient)
    {
        _blobClient = blobClient;
    }

    public required DccAlertReaderInput Input { get; init; }
    public DccAlertReaderResult Result { get; set; } = default!;

    public async Task Execute()
    {
        var client = _blobClient ?? DccXmlBlobClient.Create(Input.XmlContainerName);
        var xml = await client.DownloadTextAsync(Input.XmlBlobName);
        var validation = DccXmlValidator.Validate(xml, Input.XsdPathList);
        DccXmlValidator.ThrowIfInvalid(validation);

        var parsed = DccResponseParser.Parse(xml);
        if (!string.Equals(parsed.MessageType, "DCCAlertMessage", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Expected DCCAlertMessage but received '{parsed.MessageType}'.");
        }

        DccResponseParser.EnsureMatches(parsed, Input.ExpectedRequestId, "", "");
        var commandVariant = DccIdentifiers.Normalize(Input.ExpectedCommandVariant, "");
        Result = new DccAlertReaderResult
        {
            RequestId = parsed.RequestId,
            ResponseId = parsed.ResponseId,
            ResponseCode = parsed.ResponseCode,
            ResponseDateTime = parsed.ResponseDateTime,
            CommandVariant = commandVariant,
            MessageType = parsed.MessageType,
            AlertCode = parsed.AlertCode,
            AlertType = parsed.AlertType,
            AlertText = parsed.PayloadText,
            ThrottledAlertSequenceId = parsed.ThrottledAlertSequenceId,
            ThrottledAlertCount = parsed.ThrottledAlertCount,
            RawXmlBlobName = Input.XmlBlobName,
            XmlContainerName = client.ContainerName,
            ParsedAt = DateTimeOffset.UtcNow.ToString("O"),
            ValidationStatus = validation.Status,
            ValidationErrors = validation.Errors,
            CorrelationKey = DccCorrelation.BuildKey(parsed.RequestId, "", "", commandVariant)
        };
    }
}
