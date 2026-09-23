using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BorsdataMcp;

public sealed record CursorPage<T>(IReadOnlyList<T> Items, int TotalMatched, string? NextCursor);

public sealed class CursorPaginationService(
    IMemoryCache cache,
    IOptions<ScreeningOptions> options,
    TimeProvider timeProvider)
{
    private const string SnapshotKeyPrefix = "pagination:snapshot:";
    private const string CursorKeyPrefix = "pagination:cursor:";

    public CursorPage<T> CreatePage<T>(string owner, IEnumerable<T> results, int? requestedPageSize)
    {
        ValidateConfiguration();
        var pageSize = requestedPageSize ?? options.Value.DefaultPageSize;
        if (pageSize < 1 || pageSize > options.Value.MaxPageSize)
            throw InvalidRequest($"pageSize must be between 1 and {options.Value.MaxPageSize}.");

        var materialized = results.ToImmutableArray();
        if (materialized.Length <= pageSize)
            return new CursorPage<T>(materialized, materialized.Length, null);

        var now = timeProvider.GetUtcNow();
        var snapshot = new Snapshot<T>(
            Guid.NewGuid().ToString("N"),
            owner,
            materialized,
            pageSize,
            now.Add(options.Value.SnapshotTtl));
        SetCacheEntry(SnapshotKey(snapshot.Id), snapshot, snapshot.ExpiresAt);
        return RenderPage(snapshot, 0);
    }

    public CursorPage<T> GetNextPage<T>(string owner, string cursor)
    {
        ValidateConfiguration();
        if (string.IsNullOrWhiteSpace(cursor) ||
            !cache.TryGetValue(CursorKey(cursor), out CursorEntry? entry) ||
            entry is null || entry.Owner != owner || timeProvider.GetUtcNow() >= entry.ExpiresAt ||
            !cache.TryGetValue(SnapshotKey(entry.SnapshotId), out Snapshot<T>? snapshot) ||
            snapshot is null || snapshot.Owner != owner || timeProvider.GetUtcNow() >= snapshot.ExpiresAt)
        {
            throw CursorExpired();
        }

        return RenderPage(snapshot, entry.NextOffset);
    }

    private CursorPage<T> RenderPage<T>(Snapshot<T> snapshot, int offset)
    {
        var page = snapshot.Results.Skip(offset).Take(snapshot.PageSize).ToArray();
        var nextOffset = offset + page.Length;
        string? nextCursor = null;

        if (nextOffset < snapshot.Results.Length)
        {
            nextCursor = CreateToken();
            SetCacheEntry(
                CursorKey(nextCursor),
                new CursorEntry(snapshot.Id, snapshot.Owner, nextOffset, snapshot.ExpiresAt),
                snapshot.ExpiresAt);
        }

        return new CursorPage<T>(page, snapshot.Results.Length, nextCursor);
    }

    private void ValidateConfiguration()
    {
        if (options.Value.DefaultPageSize < 1 ||
            options.Value.MaxPageSize < options.Value.DefaultPageSize ||
            options.Value.SnapshotTtl <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Invalid pagination configuration.");
        }
    }

    private void SetCacheEntry<T>(string key, T value, DateTimeOffset expiresAt) where T : notnull
    {
        var remainingLifetime = expiresAt - timeProvider.GetUtcNow();
        if (remainingLifetime <= TimeSpan.Zero)
            return;

        cache.Set(key, value, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = remainingLifetime
        });
    }

    private static string CreateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string SnapshotKey(string snapshotId) => SnapshotKeyPrefix + snapshotId;

    private static string CursorKey(string cursor) => CursorKeyPrefix + cursor;

    private static InvalidOperationException InvalidRequest(string detail) =>
        new($"INVALID_REQUEST: {detail}");

    private static InvalidOperationException CursorExpired() =>
        new("CURSOR_EXPIRED: The cursor is invalid or expired. Start a new request.");

    private sealed record Snapshot<T>(
        string Id,
        string Owner,
        ImmutableArray<T> Results,
        int PageSize,
        DateTimeOffset ExpiresAt);

    private sealed record CursorEntry(
        string SnapshotId,
        string Owner,
        int NextOffset,
        DateTimeOffset ExpiresAt);
}
