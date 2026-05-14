using System.ComponentModel;

namespace Backend.Plugins.Configuration.Dcc;

public sealed class GatewayOptions
{
    [Description("DCC gateway endpoint used when a plugin input does not provide an endpoint.")]
    public Uri? EndpointUrl { get; init; }

    [Description("HTTP method used for DCC gateway submissions.")]
    public string? HttpMethod { get; init; }

    [Description("HTTP timeout used for DCC gateway submissions.")]
    public TimeSpan? Timeout { get; init; }

    [Description("Optional bearer token used by local or mocked DCC gateway transports.")]
    public Secret<string>? ApiKey { get; init; }
}
