using System.Net;
using Microsoft.Extensions.Logging;

namespace BorsdataMcp;

// Must be registered transient in Program.cs, NOT singleton — confirmed live in a real,
// long-running Claude Desktop session that a singleton registration crashes the server:
// IHttpClientFactory rebuilds its handler pipeline periodically (default every 2 minutes) and
// requires every DelegatingHandler in that pipeline to have a null InnerHandler at build time.
// Reusing the same handler instance across rebuilds throws "The 'InnerHandler' property must be
// null. 'DelegatingHandler' instances provided to 'HttpMessageHandlerBuilder' must not be reused
// or cached" the moment the first rebuild happens — after which every subsequent HTTP call fails
// the same way, permanently, until the process restarts. (This is exactly why none of this
// project's own testing ever caught it: every manual run-dev.sh test session was well under 2
// minutes, so the pipeline never lived long enough to rebuild.) The state that actually needs to
// survive rebuilds (the rate limiter and the daily counter) lives in the injected singleton
// RateLimitState instead — that state persists across rebuilds even though this handler doesn't.
public sealed class RateLimitHandler(RateLimitState state, ILogger<RateLimitHandler> logger) : DelegatingHandler
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(10);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        var attempt = 0;

        while (true)
        {
            attempt++;

            using (var lease = await state.Limiter.AcquireAsync(1, cancellationToken))
            {
                if (!lease.IsAcquired)
                {
                    throw new HttpRequestException("Börsdata API rate limiter queue is full — too many concurrent requests.");
                }

                state.TrackDailyCount(logger);

                response = await base.SendAsync(request, cancellationToken);
            }

            if (response.StatusCode != (HttpStatusCode)429 || attempt >= MaxAttempts)
            {
                return response;
            }

            var retryAfter = response.Headers.RetryAfter?.Delta ?? DefaultRetryAfter;
            logger.LogWarning("Börsdata API returned 429 (attempt {Attempt}/{MaxAttempts}); waiting {RetryAfter} before retrying.", attempt, MaxAttempts, retryAfter);
            response.Dispose();
            await Task.Delay(retryAfter, cancellationToken);
        }
    }
}
