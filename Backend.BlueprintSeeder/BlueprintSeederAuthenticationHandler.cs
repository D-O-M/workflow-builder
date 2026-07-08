using System.Net.Http.Headers;

namespace Backend.BlueprintSeeder;

internal sealed class BlueprintSeederAuthenticationHandler : DelegatingHandler
{
    private readonly BlueprintSeederAccessTokenProvider _accessTokenProvider;

    public BlueprintSeederAuthenticationHandler(BlueprintSeederAccessTokenProvider accessTokenProvider)
    {
        _accessTokenProvider = accessTokenProvider;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var accessToken = await _accessTokenProvider.GetAccessTokenAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
