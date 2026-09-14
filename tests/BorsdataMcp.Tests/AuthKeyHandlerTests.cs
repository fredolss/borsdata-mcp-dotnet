using BorsdataMcp;
using Microsoft.Extensions.Options;

namespace BorsdataMcp.Tests;

public class AuthKeyHandlerTests
{
    private sealed class RecordingHandler : DelegatingHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    [Fact]
    public async Task SendAsync_AppendsAuthKey_WhenUrlHasNoQuery()
    {
        var recorder = new RecordingHandler();
        var handler = new AuthKeyHandler(Options.Create(new BorsdataOptions { ApiKey = "test-key" }))
        {
            InnerHandler = recorder
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://apiservice.borsdata.se/v1/") };

        await client.GetAsync("instruments");

        Assert.Equal("https://apiservice.borsdata.se/v1/instruments?authKey=test-key", recorder.LastRequestUri?.ToString());
    }

    [Fact]
    public async Task SendAsync_AppendsAuthKey_WhenUrlAlreadyHasQuery()
    {
        var recorder = new RecordingHandler();
        var handler = new AuthKeyHandler(Options.Create(new BorsdataOptions { ApiKey = "test-key" }))
        {
            InnerHandler = recorder
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://apiservice.borsdata.se/v1/") };

        await client.GetAsync("instruments/1/stockprices?maxCount=5");

        Assert.Equal("https://apiservice.borsdata.se/v1/instruments/1/stockprices?maxCount=5&authKey=test-key", recorder.LastRequestUri?.ToString());
    }

    [Fact]
    public async Task SendAsync_LeavesUrlUnchanged_WhenApiKeyIsEmpty()
    {
        var recorder = new RecordingHandler();
        var handler = new AuthKeyHandler(Options.Create(new BorsdataOptions { ApiKey = "" }))
        {
            InnerHandler = recorder
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://apiservice.borsdata.se/v1/") };

        await client.GetAsync("instruments");

        Assert.Equal("https://apiservice.borsdata.se/v1/instruments", recorder.LastRequestUri?.ToString());
    }
}
