using Microsoft.Extensions.Options;

namespace BorsdataMcp;

public sealed class AuthKeyHandler(IOptions<BorsdataOptions> options) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var apiKey = options.Value.ApiKey;
        if (!string.IsNullOrEmpty(apiKey) && request.RequestUri is not null)
        {
            var separator = string.IsNullOrEmpty(request.RequestUri.Query) ? "?" : "&";
            request.RequestUri = new Uri($"{request.RequestUri}{separator}authKey={Uri.EscapeDataString(apiKey)}");
        }

        return base.SendAsync(request, cancellationToken);
    }
}
