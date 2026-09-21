using BorsdataMcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

var builder = Host.CreateApplicationBuilder(args);

// The MCP stdio transport uses stdout for protocol messages, so all logging must go to stderr.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.Configure<BorsdataOptions>(builder.Configuration.GetSection("Borsdata"));
builder.Services.Configure<ScreeningOptions>(builder.Configuration.GetSection("Screening"));

builder.Services.AddMemoryCache();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddTransient<InstrumentScreeningService>();

builder.Services.AddTransient<AuthKeyHandler>();
// RateLimitState (the rate limiter + daily counter) is the singleton, not RateLimitHandler
// itself — IHttpClientFactory periodically rebuilds the handler pipeline (default every 2
// minutes) and requires a fresh DelegatingHandler instance each time. A singleton handler used to
// be registered here to keep the rate-limiting state alive across those rebuilds, but that
// crashed the server the moment the first rebuild happened (see RateLimitHandler's own comment).
builder.Services.AddSingleton<RateLimitState>();
builder.Services.AddTransient<RateLimitHandler>();
builder.Services
    .AddHttpClient<BorsdataApiClient>((services, http) =>
    {
        var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<BorsdataOptions>>().Value;
        http.BaseAddress = new Uri(options.BaseUrl);
    })
    .AddHttpMessageHandler<AuthKeyHandler>()
    .AddHttpMessageHandler<RateLimitHandler>();

// Only exceptions of type McpException have their .Message forwarded to the calling MCP
// client (ModelContextProtocol.Server.McpServerImpl.CreateToolCallErrorResult) — every other
// exception collapses to a bare "An error occurred invoking 'X'." with zero detail. Confirmed
// live: a calling model that hit this got no information to self-correct on, retried an
// identical failing call six times, then abandoned the bulk tool entirely for a ~164-call
// per-instrument fallback. This filter converts any other exception into one whose message
// does get forwarded, project-wide, for every tool regardless of registration path.
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithRequestFilters(f => f.AddCallToolFilter(next => async (request, ct) =>
    {
        try
        {
            return await next(request, ct);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException(ex.Message, ex);
        }
    }));

await builder.Build().RunAsync();
