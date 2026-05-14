using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Dcc.Common;
using Dcc.DccAlertReader;
using Dcc.DeviceAlertReader;
using Dcc.DuisResponseReader;
using Dcc.DuisServiceRequestWriter;
using Dcc.GatewaySubmit;
using Dcc.ParseRequestWriter;
using Shouldly;

namespace Backend.Dcc.Tests;

public sealed class DccPluginPackTests
{
    private const string RequestId = "00-00-00-00-00-00-00-01:00-00-00-00-00-00-00-02:10001";

    [Test]
    public async Task Duis_service_request_writer_uploads_deterministic_duis_xml_and_metadata()
    {
        var blobs = new InMemoryDccXmlBlobClient();
        var plugin = new DuisServiceRequestWriterPlugin(blobs)
        {
            Input = new DuisServiceRequestWriterInput
            {
                RequestId = RequestId,
                CommandVariant = "1",
                ServiceReference = "4.8",
                ServiceReferenceVariant = "4.8.1",
                BodyElementName = "ReadInventory",
                DeviceId = "01-02-03-04-05-06-07-08",
                XmlBlobName = "dcc/requests/sample.xml"
            }
        };

        await plugin.Execute();

        var xml = await blobs.DownloadTextAsync("dcc/requests/sample.xml");
        xml.ShouldContain("<sr:Request");
        xml.ShouldContain("schemaVersion=\"5.4\"");
        xml.ShouldContain("<sr:RequestID>" + RequestId + "</sr:RequestID>");
        xml.ShouldContain("<sr:CommandVariant>1</sr:CommandVariant>");
        xml.ShouldContain("<sr:ServiceReference>4.8</sr:ServiceReference>");
        xml.ShouldContain("<sr:DeviceID>01-02-03-04-05-06-07-08</sr:DeviceID>");
        plugin.Result.CorrelationKey.ShouldBe(RequestId + "|4.8|4.8.1|1");
        plugin.Result.ValidationStatus.ShouldBe("WellFormed");
    }

    [Test]
    public async Task Gateway_submit_mock_response_round_trips_to_duis_response_reader()
    {
        var blobs = new InMemoryDccXmlBlobClient();
        var writer = new DuisServiceRequestWriterPlugin(blobs)
        {
            Input = new DuisServiceRequestWriterInput
            {
                RequestId = RequestId,
                CommandVariant = "1",
                ServiceReference = "4.8",
                ServiceReferenceVariant = "4.8.1",
                DeviceId = "01-02-03-04-05-06-07-08",
                XmlBlobName = "dcc/requests/request.xml"
            }
        };
        await writer.Execute();

        var submit = new GatewaySubmitPlugin(blobs)
        {
            Input = new GatewaySubmitInput
            {
                XmlBlobName = writer.Result.XmlBlobName,
                RequestId = writer.Result.RequestId,
                ServiceReference = writer.Result.ServiceReference,
                ServiceReferenceVariant = writer.Result.ServiceReferenceVariant,
                CommandVariant = writer.Result.CommandVariant,
                ResponseXmlBlobName = "dcc/responses/response.xml"
            }
        };
        await submit.Execute();

        var reader = new DuisResponseReaderPlugin(blobs)
        {
            Input = new DuisResponseReaderInput
            {
                XmlBlobName = submit.Result.ResponseXmlBlobName,
                ExpectedRequestId = RequestId,
                ExpectedServiceReference = "4.8",
                ExpectedServiceReferenceVariant = "4.8.1",
                ExpectedCommandVariant = "1"
            }
        };
        await reader.Execute();

        reader.Result.RequestId.ShouldBe(RequestId);
        reader.Result.ResponseCode.ShouldBe("I0");
        reader.Result.MessageType.ShouldBe("ResponseMessage");
        reader.Result.PayloadElementName.ShouldBe("GBCSPayload");
        reader.Result.CorrelationKey.ShouldBe(RequestId + "|4.8|4.8.1|1");
    }

    [Test]
    public async Task Duis_response_reader_rejects_request_correlation_mismatch()
    {
        var blobs = new InMemoryDccXmlBlobClient();
        await blobs.UploadTextAsync("response.xml", SolicitedResponseXml(RequestId));

        var plugin = new DuisResponseReaderPlugin(blobs)
        {
            Input = new DuisResponseReaderInput
            {
                XmlBlobName = "response.xml",
                ExpectedRequestId = "00-00-00-00-00-00-00-09:00-00-00-00-00-00-00-02:99999",
                ExpectedServiceReference = "4.8",
                ExpectedServiceReferenceVariant = "4.8.1"
            }
        };

        var exception = await Should.ThrowAsync<InvalidDataException>(() => plugin.Execute());
        exception.Message.ShouldContain("DCC correlation mismatch for RequestID");
    }

    [Test]
    public async Task Device_alert_reader_extracts_flat_alert_model()
    {
        var blobs = new InMemoryDccXmlBlobClient();
        await blobs.UploadTextAsync("device-alert.xml", DeviceAlertXml(RequestId));

        var plugin = new DeviceAlertReaderPlugin(blobs)
        {
            Input = new DeviceAlertReaderInput
            {
                XmlBlobName = "device-alert.xml",
                ExpectedRequestId = RequestId,
                ExpectedCommandVariant = "1"
            }
        };

        await plugin.Execute();

        plugin.Result.MessageType.ShouldBe("DeviceAlertMessage");
        plugin.Result.AlertCode.ShouldBe("8F66");
        plugin.Result.ThrottledAlertCount.ShouldBe("2");
        plugin.Result.GbcsPayloadBase64.ShouldBe("VEVTVA==");
    }

    [Test]
    public async Task Dcc_alert_reader_extracts_flat_alert_model()
    {
        var blobs = new InMemoryDccXmlBlobClient();
        await blobs.UploadTextAsync("dcc-alert.xml", DccAlertXml(RequestId));

        var plugin = new DccAlertReaderPlugin(blobs)
        {
            Input = new DccAlertReaderInput
            {
                XmlBlobName = "dcc-alert.xml",
                ExpectedRequestId = RequestId,
                ExpectedCommandVariant = "1"
            }
        };

        await plugin.Execute();

        plugin.Result.MessageType.ShouldBe("DCCAlertMessage");
        plugin.Result.AlertCode.ShouldBe("N1");
        plugin.Result.AlertType.ShouldBe("PowerOutageEvent");
        plugin.Result.AlertText.ShouldContain("01-02-03-04-05-06-07-08");
    }

    [Test]
    public void Response_parser_reads_parse_output_shape()
    {
        var parsed = DccResponseParser.Parse(
            """
            <sr:ParseOutput xmlns:sr="http://www.dccinterface.co.uk/ServiceUserGateway">
              <sr:TranslatedPayload>OK</sr:TranslatedPayload>
            </sr:ParseOutput>
            """);

        parsed.MessageType.ShouldBe("ParseOutput");
        parsed.PayloadElementName.ShouldBe("TranslatedPayload");
        parsed.PayloadText.ShouldBe("OK");
    }

    [Test]
    public async Task Parse_request_writer_uploads_parse_request_xml()
    {
        var blobs = new InMemoryDccXmlBlobClient();
        var plugin = new ParseRequestWriterPlugin(blobs)
        {
            Input = new ParseRequestWriterInput
            {
                RequestId = RequestId,
                CommandVariant = "4",
                ServiceReference = "4.8",
                ServiceReferenceVariant = "4.8.1",
                GbcsPayloadBase64 = "VEVTVA==",
                XmlBlobName = "parse-request.xml"
            }
        };

        await plugin.Execute();

        var xml = await blobs.DownloadTextAsync("parse-request.xml");
        xml.ShouldContain("<sr:ParseRequest");
        xml.ShouldContain("<sr:GBCSPayload>VEVTVA==</sr:GBCSPayload>");
        plugin.Result.CorrelationKey.ShouldBe(RequestId + "|4.8|4.8.1|4");
    }

    [Test]
    public void Validator_reports_malformed_xml()
    {
        var report = DccXmlValidator.Validate("<Response>", "");

        report.Status.ShouldBe("Invalid");
        report.Errors.ShouldNotBeEmpty();
    }

    [Test]
    public void Validator_reports_schema_validation_failure()
    {
        var schemaPath = Path.Combine(Path.GetTempPath(), "dcc-test-schema-" + Guid.NewGuid().ToString("N") + ".xsd");
        File.WriteAllText(
            schemaPath,
            """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"
                       targetNamespace="http://www.dccinterface.co.uk/ServiceUserGateway"
                       xmlns:sr="http://www.dccinterface.co.uk/ServiceUserGateway"
                       elementFormDefault="qualified">
              <xs:element name="Response">
                <xs:complexType>
                  <xs:sequence>
                    <xs:element name="RequiredChild" type="xs:string" />
                  </xs:sequence>
                </xs:complexType>
              </xs:element>
            </xs:schema>
            """);

        try
        {
            var report = DccXmlValidator.Validate(
                """<sr:Response xmlns:sr="http://www.dccinterface.co.uk/ServiceUserGateway" />""",
                schemaPath);

            report.Status.ShouldBe("Invalid");
            report.Errors.ShouldContain("RequiredChild");
        }
        finally
        {
            File.Delete(schemaPath);
        }
    }

    [Test]
    public async Task Reader_plugin_surfaces_missing_blob_failure()
    {
        var plugin = new DuisResponseReaderPlugin(new InMemoryDccXmlBlobClient())
        {
            Input = new DuisResponseReaderInput
            {
                XmlBlobName = "missing.xml"
            }
        };

        var exception = await Should.ThrowAsync<FileNotFoundException>(() => plugin.Execute());
        exception.Message.ShouldContain("missing.xml");
    }

    private static string SolicitedResponseXml(string requestId) =>
        $$"""
        <sr:Response xmlns:sr="http://www.dccinterface.co.uk/ServiceUserGateway" schemaVersion="5.4">
          <sr:Header>
            <sr:RequestID>{{requestId}}</sr:RequestID>
            <sr:ResponseID>{{requestId}}</sr:ResponseID>
            <sr:ResponseCode>I0</sr:ResponseCode>
            <sr:ResponseDateTime>2026-05-13T10:00:00Z</sr:ResponseDateTime>
          </sr:Header>
          <sr:Body>
            <sr:ResponseMessage>
              <sr:ServiceReference>4.8</sr:ServiceReference>
              <sr:ServiceReferenceVariant>4.8.1</sr:ServiceReferenceVariant>
              <sr:GBCSPayload>VEVTVA==</sr:GBCSPayload>
            </sr:ResponseMessage>
          </sr:Body>
        </sr:Response>
        """;

    private static string DeviceAlertXml(string requestId) =>
        $$"""
        <sr:Response xmlns:sr="http://www.dccinterface.co.uk/ServiceUserGateway" schemaVersion="5.4">
          <sr:Header>
            <sr:RequestID>{{requestId}}</sr:RequestID>
            <sr:ResponseID>{{requestId}}</sr:ResponseID>
            <sr:ResponseCode>I0</sr:ResponseCode>
            <sr:ResponseDateTime>2026-05-13T10:00:00Z</sr:ResponseDateTime>
          </sr:Header>
          <sr:Body>
            <sr:DeviceAlertMessage>
              <sr:AlertCode>8F66</sr:AlertCode>
              <sr:ThrottledAlertSequenceId>7</sr:ThrottledAlertSequenceId>
              <sr:ThrottledAlertCount>2</sr:ThrottledAlertCount>
              <sr:GBCSPayload>VEVTVA==</sr:GBCSPayload>
            </sr:DeviceAlertMessage>
          </sr:Body>
        </sr:Response>
        """;

    private static string DccAlertXml(string requestId) =>
        $$"""
        <sr:Response xmlns:sr="http://www.dccinterface.co.uk/ServiceUserGateway" schemaVersion="5.4">
          <sr:Header>
            <sr:RequestID>{{requestId}}</sr:RequestID>
            <sr:ResponseID>{{requestId}}</sr:ResponseID>
            <sr:ResponseCode>I0</sr:ResponseCode>
            <sr:ResponseDateTime>2026-05-13T10:00:00Z</sr:ResponseDateTime>
          </sr:Header>
          <sr:Body>
            <sr:DCCAlertMessage>
              <sr:DCCAlertCode>N1</sr:DCCAlertCode>
              <sr:DCCAlert>
                <sr:PowerOutageEvent>
                  <sr:CommsHubDeviceID>01-02-03-04-05-06-07-08</sr:CommsHubDeviceID>
                  <sr:StartDateTime>2026-05-13T10:00:00Z</sr:StartDateTime>
                  <sr:MPxN>1000000000001</sr:MPxN>
                </sr:PowerOutageEvent>
              </sr:DCCAlert>
            </sr:DCCAlertMessage>
          </sr:Body>
        </sr:Response>
        """;

    private sealed class InMemoryDccXmlBlobClient : IDccXmlBlobClient
    {
        private readonly Dictionary<string, string> _blobs = new(StringComparer.OrdinalIgnoreCase);

        public string ContainerName => "test-dcc";

        public Task UploadTextAsync(string blobName, string content, string contentType = "application/xml")
        {
            _blobs[blobName] = content;
            return Task.CompletedTask;
        }

        public Task<string> DownloadTextAsync(string blobName)
        {
            if (!_blobs.TryGetValue(blobName, out var content))
            {
                throw new FileNotFoundException($"Blob '{blobName}' does not exist.", blobName);
            }

            return Task.FromResult(content);
        }
    }
}
