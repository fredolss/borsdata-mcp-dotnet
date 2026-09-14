using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;

namespace BorsdataMcp;

// Holds the state that must survive across IHttpClientFactory's periodic handler-pipeline
// rebuilds (the token-bucket limiter and the soft daily counter), registered as a singleton and
// injected into RateLimitHandler — which itself must stay transient (see its own class comment).
// Splitting the state out like this, instead of making RateLimitHandler itself the singleton (the
// previous design), is what actually fixes the "InnerHandler must be null" crash: the state lives
// on across rebuilds, but the DelegatingHandler instance handed to each new pipeline does not.
public sealed class RateLimitState
{
    private const int DailySoftLimit = 10_000;
    private static readonly TimeSpan DailyWindow = TimeSpan.FromHours(24);

    public RateLimiter Limiter { get; } = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
    {
        PermitLimit = 100,
        Window = TimeSpan.FromSeconds(10),
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = 1000
    });

    private readonly Lock _dailyCountLock = new();
    private DateTimeOffset _dailyWindowStart = DateTimeOffset.UtcNow;
    private int _dailyCount;
    private bool _dailyWarningLogged;

    public void TrackDailyCount(ILogger logger)
    {
        lock (_dailyCountLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _dailyWindowStart >= DailyWindow)
            {
                _dailyWindowStart = now;
                _dailyCount = 0;
                _dailyWarningLogged = false;
            }

            _dailyCount++;

            if (_dailyCount > DailySoftLimit && !_dailyWarningLogged)
            {
                _dailyWarningLogged = true;
                logger.LogWarning("Börsdata API call count has exceeded the recommended soft limit of {DailySoftLimit} per 24h.", DailySoftLimit);
            }
        }
    }
}
