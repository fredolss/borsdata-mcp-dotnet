using BorsdataMcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// The MCP stdio transport uses stdout for protocol messages, so all logging must go to stderr.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.Configure<BorsdataOptions>(builder.Configuration.GetSection("Borsdata"));

builder.Services.AddMemoryCache();

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

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
