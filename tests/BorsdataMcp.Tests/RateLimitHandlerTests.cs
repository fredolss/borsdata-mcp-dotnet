using System.Net;
using System.Net.Http.Headers;
using BorsdataMcp;
using Microsoft.Extensions.Logging.Abstractions;

namespace BorsdataMcp.Tests;

public class RateLimitHandlerTests
{
    private sealed class StubHandler : DelegatingHandler
    {
        private readonly Func<int, HttpResponseMessage> _responder;
        public int CallCount { get; private set; }

        public StubHandler(Func<int, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_responder(CallCount));
        }
    }

    private static HttpResponseMessage TooManyRequests() => new((HttpStatusCode)429)
    {
        Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1)) }
    };

    [Fact]
    public async Task SendAsync_RetriesAfter429_AndSucceeds()
    {
        var stub = new StubHandler(callNumber => callNumber == 1 ? TooManyRequests() : new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new RateLimitHandler(new RateLimitState(), NullLogger<RateLimitHandler>.Instance) { InnerHandler = stub };
        using var client = new HttpClient(handler);

        var response = await client.GetAsync("https://example.test/instruments");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, stub.CallCount);
    }

    [Fact]
    public async Task SendAsync_GivesUpAfterMaxAttempts_WhenAlways429()
    {
        var stub = new StubHandler(_ => TooManyRequests());
        var handler = new RateLimitHandler(new RateLimitState(), NullLogger<RateLimitHandler>.Instance) { InnerHandler = stub };
        using var client = new HttpClient(handler);

        var response = await client.GetAsync("https://example.test/instruments");

        Assert.Equal((HttpStatusCode)429, response.StatusCode);
        Assert.Equal(3, stub.CallCount);
    }

    [Fact]
    public async Task SendAsync_PassesThrough_WhenNot429()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new RateLimitHandler(new RateLimitState(), NullLogger<RateLimitHandler>.Instance) { InnerHandler = stub };
        using var client = new HttpClient(handler);

        var response = await client.GetAsync("https://example.test/instruments");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, stub.CallCount);
    }

    [Fact]
    public async Task MultipleHandlerInstances_CanShareTheSameState_WithoutThrowing()
    {
        // Regression test for a real crash found in a live Claude Desktop session: RateLimitHandler
        // used to be registered as a singleton so its state would survive IHttpClientFactory's
        // periodic pipeline rebuilds (default every 2 minutes) — but that meant the same
        // DelegatingHandler instance got reused across pipelines, which throws "The 'InnerHandler'
        // property must be null..." the moment a rebuild actually happens (every manual test in
        // this project ran under 2 minutes, so it was never caught until a real long-running
        // session hit it). The fix: RateLimitState is the singleton now, injected into a transient
        // RateLimitHandler. Two separate RateLimitHandler instances — like two separate pipeline
        // builds would produce — must be constructible and independently usable while still
        // sharing the same underlying state.
        var state = new RateLimitState();
        var stub1 = new StubHandler(_ => TooManyRequests());
        using var handler1 = new RateLimitHandler(state, NullLogger<RateLimitHandler>.Instance) { InnerHandler = stub1 };
        using var client1 = new HttpClient(handler1);

        await client1.GetAsync("https://example.test/instruments");
        Assert.Equal(3, stub1.CallCount);

        var stub2 = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var handler2 = new RateLimitHandler(state, NullLogger<RateLimitHandler>.Instance) { InnerHandler = stub2 };
        using var client2 = new HttpClient(handler2);

        var response = await client2.GetAsync("https://example.test/instruments");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, stub2.CallCount);
    }
}
