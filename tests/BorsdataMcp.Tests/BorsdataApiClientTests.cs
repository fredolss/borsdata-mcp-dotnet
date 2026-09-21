using System.Net;
using System.Text;
using BorsdataMcp.Tools;
using Microsoft.Extensions.Caching.Memory;

namespace BorsdataMcp.Tests;

public class BorsdataApiClientTests
{
    private sealed class FixedResponseHandler(HttpStatusCode statusCode, string? body) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = body is null ? null : new StringContent(body, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    private static BorsdataApiClient CreateClient(HttpStatusCode statusCode, string? body)
    {
        var httpClient = new HttpClient(new FixedResponseHandler(statusCode, body))
        {
            BaseAddress = new Uri("https://apiservice.borsdata.se/v1/")
        };
        return new BorsdataApiClient(httpClient, new MemoryCache(new MemoryCacheOptions()));
    }

    [Fact]
    public async Task GetAsync_NonSuccessStatus_IncludesResponseBodyInExceptionMessage()
    {
        var client = CreateClient(HttpStatusCode.BadRequest, """{ "errorMessage": "Invalid calcGroup for this kpiId" }""");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetMarketsAsync());

        Assert.Contains("400", ex.Message);
        Assert.Contains("Invalid calcGroup for this kpiId", ex.Message);
    }

    [Fact]
    public async Task GetAsync_NonSuccessStatus_TruncatesVeryLongResponseBody()
    {
        var longBody = new string('x', 2000);
        var client = CreateClient(HttpStatusCode.InternalServerError, longBody);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetMarketsAsync());

        Assert.True(ex.Message.Length < longBody.Length);
        Assert.Contains("...", ex.Message);
    }

    [Fact]
    public async Task GetAsync_NonSuccessStatus_NoBody_StillProducesReadableMessage()
    {
        var client = CreateClient(HttpStatusCode.Forbidden, body: null);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetMarketsAsync());

        Assert.Contains("403", ex.Message);
        Assert.Contains("Forbidden", ex.Message);
    }
}
