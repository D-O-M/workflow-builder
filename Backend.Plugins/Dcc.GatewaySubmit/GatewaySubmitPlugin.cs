using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Backend.Plugins.Configuration.Dcc;
using Backend.Plugins.Harness;
using Dcc.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dcc.GatewaySubmit;

public sealed class GatewaySubmitPlugin : IPlugin
{
    private readonly HttpClient _httpClient;
    private readonly IDccXmlBlobClient? _blobClient;
    private readonly GatewayOptions _gatewayOptions;
    private readonly bool _ownsHttpClient;

    public GatewaySubmitPlugin()
        : this(new HttpClient(), null, new GatewayOptions(), ownsHttpClient: true)
    {
    }

    [ActivatorUtilitiesConstructor]
    public GatewaySubmitPlugin(IOptions<GatewayOptions> gatewayOptions)
        : this(new HttpClient(), null, gatewayOptions.Value, ownsHttpClient: true)
    {
    }

    public GatewaySubmitPlugin(IDccXmlBlobClient blobClient)
        : this(new HttpClient(), blobClient, new GatewayOptions(), ownsHttpClient: true)
    {
    }

    public GatewaySubmitPlugin(HttpClient httpClient, IDccXmlBlobClient? blobClient = null)
        : this(httpClient, blobClient, new GatewayOptions(), ownsHttpClient: false)
    {
    }

    private GatewaySubmitPlugin(
        HttpClient httpClient,
        IDccXmlBlobClient? blobClient,
        GatewayOptions gatewayOptions,
        bool ownsHttpClient)
    {
        _httpClient = httpClient;
        _blobClient = blobClient;
        _gatewayOptions = gatewayOptions;
        _ownsHttpClient = ownsHttpClient;
    }

    public required GatewaySubmitInput Input { get; init; }
    public GatewaySubmitResult Result { get; set; } = default!;

    public async Task Execute()
    {
        var client = _blobClient ?? DccXmlBlobClient.Create(Input.XmlContainerName);
        var requestXml = await client.DownloadTextAsync(Input.XmlBlobName);
        var requestMetadata = ReadRequestMetadata(requestXml);
        var requestId = DccIdentifiers.Normalize(Input.RequestId, requestMetadata.RequestId);
        var serviceReference = DccIdentifiers.Normalize(Input.ServiceReference, requestMetadata.ServiceReference);
        var serviceReferenceVariant = DccIdentifiers.Normalize(Input.ServiceReferenceVariant, requestMetadata.ServiceReferenceVariant);
        var commandVariant = DccIdentifiers.Normalize(Input.CommandVariant, requestMetadata.CommandVariant);
        var correlationKey = DccCorrelation.BuildKey(requestId, serviceReference, serviceReferenceVariant, commandVariant);

        var endpointUrl = ResolveEndpointUrl();
        var response = endpointUrl is null
            ? await CreateMockResponseAsync(requestXml, requestId, serviceReference, serviceReferenceVariant)
            : await SubmitToGatewayAsync(requestXml, endpointUrl);

        var responseBlobName = DccIdentifiers.Normalize(
            Input.ResponseXmlBlobName,
            DccXmlBlobNames.Response(correlationKey, requestId));

        await client.UploadTextAsync(responseBlobName, response.Xml);
        if (!response.Succeeded)
        {
            throw new InvalidOperationException(
                $"DCC gateway submission failed with status {(int)response.StatusCode}: {response.TransportStatus}");
        }

        Result = new GatewaySubmitResult
        {
            RequestId = requestId,
            ServiceReference = serviceReference,
            ServiceReferenceVariant = serviceReferenceVariant,
            CommandVariant = commandVariant,
            RequestXmlBlobName = Input.XmlBlobName,
            ResponseXmlBlobName = responseBlobName,
            XmlContainerName = client.ContainerName,
            CorrelationKey = correlationKey,
            Succeeded = response.Succeeded,
            StatusCode = (int)response.StatusCode,
            TransportStatus = response.TransportStatus,
            ResponseLength = response.Xml.Length,
            SubmittedAt = DateTimeOffset.UtcNow.ToString("O")
        };
    }

    private async Task<GatewaySubmissionResponse> SubmitToGatewayAsync(string requestXml, Uri endpointUrl)
    {
        var timeout = ResolveTimeout();
        if (_ownsHttpClient && timeout is not null)
        {
            _httpClient.Timeout = timeout.Value;
        }

        using var request = new HttpRequestMessage(
            new HttpMethod(DccIdentifiers.Normalize(Input.HttpMethod, _gatewayOptions.HttpMethod ?? "POST")),
            endpointUrl)
        {
            Content = new StringContent(requestXml, Encoding.UTF8, "application/xml")
        };

        var apiKey = _gatewayOptions.ApiKey?.Reveal();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        using var response = await _httpClient.SendAsync(request);
        var responseXml = await response.Content.ReadAsStringAsync();
        return new GatewaySubmissionResponse(
            responseXml,
            response.StatusCode,
            response.IsSuccessStatusCode,
            response.ReasonPhrase ?? response.StatusCode.ToString());
    }

    private static Task<GatewaySubmissionResponse> CreateGeneratedMockResponseAsync(
        string requestXml,
        string requestId,
        string serviceReference,
        string serviceReferenceVariant)
    {
        if (!string.IsNullOrWhiteSpace(requestXml) &&
            !string.IsNullOrWhiteSpace(requestId))
        {
            // The request XML is intentionally read before mock generation so this mode still
            // proves the blob handoff path without needing real DCC connectivity.
        }

        var xml = string.IsNullOrWhiteSpace(requestXml)
            ? ""
            : requestXml;

        if (string.IsNullOrWhiteSpace(xml))
        {
            throw new InvalidDataException("DCC request XML was empty.");
        }

        var responseXml = BuildMockResponse(
            requestId,
            serviceReference,
            serviceReferenceVariant);

        return Task.FromResult(new GatewaySubmissionResponse(
            responseXml,
            HttpStatusCode.OK,
            Succeeded: true,
            TransportStatus: "Mocked"));
    }

    private Task<GatewaySubmissionResponse> CreateMockResponseAsync(
        string requestXml,
        string requestId,
        string serviceReference,
        string serviceReferenceVariant)
    {
        if (!string.IsNullOrWhiteSpace(Input.MockResponseXml))
        {
            return Task.FromResult(new GatewaySubmissionResponse(
                Input.MockResponseXml,
                HttpStatusCode.OK,
                Succeeded: true,
                TransportStatus: "Mocked"));
        }

        return CreateGeneratedMockResponseAsync(requestXml, requestId, serviceReference, serviceReferenceVariant);
    }

    private Uri? ResolveEndpointUrl()
    {
        if (!string.IsNullOrWhiteSpace(Input.EndpointUrl))
        {
            return new Uri(Input.EndpointUrl.Trim(), UriKind.Absolute);
        }

        return _gatewayOptions.EndpointUrl;
    }

    private TimeSpan? ResolveTimeout()
    {
        if (Input.TimeoutSeconds > 0)
        {
            return TimeSpan.FromSeconds(Input.TimeoutSeconds);
        }

        return _gatewayOptions.Timeout;
    }

    private static string BuildMockResponse(
        string requestId,
        string serviceReference,
        string serviceReferenceVariant)
    {
        XNamespace sr = DccXmlConstants.ServiceUserGatewayNamespace;
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                sr + "Response",
                new XAttribute(XNamespace.Xmlns + "sr", sr),
                new XAttribute("schemaVersion", DccXmlConstants.DefaultXmlSchemaVersion),
                new XElement(
                    sr + "Header",
                    new XElement(sr + "RequestID", requestId),
                    new XElement(sr + "ResponseID", requestId),
                    new XElement(sr + "ResponseCode", "I0"),
                    new XElement(sr + "ResponseDateTime", DateTimeOffset.UtcNow.ToString("O"))),
                new XElement(
                    sr + "Body",
                    new XElement(
                        sr + "ResponseMessage",
                        new XElement(sr + "ServiceReference", serviceReference),
                        new XElement(sr + "ServiceReferenceVariant", serviceReferenceVariant),
                        new XElement(sr + "GBCSPayload", Convert.ToBase64String(Encoding.UTF8.GetBytes("Mock DCC response")))))));

        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static RequestMetadata ReadRequestMetadata(string requestXml)
    {
        var document = XDocument.Parse(requestXml, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException("DCC request XML document has no root element.");
        var header = root.Elements().FirstOrDefault(element => element.Name.LocalName == "Header")
            ?? root.Descendants().FirstOrDefault(element => element.Name.LocalName == "Header");
        if (header is null)
        {
            return new RequestMetadata("", "", "", "");
        }

        return new RequestMetadata(
            ChildValue(header, "RequestID"),
            ChildValue(header, "ServiceReference"),
            ChildValue(header, "ServiceReferenceVariant"),
            ChildValue(header, "CommandVariant"));
    }

    private static string ChildValue(XElement element, string localName) =>
        element.Elements().FirstOrDefault(child => child.Name.LocalName == localName)?.Value.Trim() ?? "";

    private sealed record RequestMetadata(
        string RequestId,
        string ServiceReference,
        string ServiceReferenceVariant,
        string CommandVariant);

    private sealed record GatewaySubmissionResponse(
        string Xml,
        HttpStatusCode StatusCode,
        bool Succeeded,
        string TransportStatus);
}
