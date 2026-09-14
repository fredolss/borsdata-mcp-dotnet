using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;

namespace BorsdataMcp;

public sealed class BorsdataApiClient(HttpClient httpClient, IMemoryCache cache)
{
    // Reference data (instruments, markets, branches, sectors, countries, kpi metadata) changes
    // rarely, so it's cached for a long time rather than refetched on every tool call. A week, not
    // just a day: confirmed live that a stdio MCP server process survives for the whole client
    // session, not just one short back-and-forth with the model — this session's own MCP connection
    // ran for hours across dozens of tool calls without restarting — so a longer TTL captures more
    // real benefit without meaningfully risking staleness for data that barely ever changes anyway.
    // Callers must
    // not mutate the returned JsonNode in place (e.g. array.Add/obj["x"]=...) since the same cached
    // instance is handed to every caller — read-only use, or DeepClone() before modifying.
    private static readonly TimeSpan ReferenceDataCacheTtl = TimeSpan.FromDays(7);

    // Market data (latest/dated stock prices, the KPI list screener) genuinely changes, but only
    // once per trading day, not continuously — confirmed live: Börsdata's "latest" close price and
    // KPI values (e.g. P/E) stayed byte-identical across several hours of testing in the same
    // session, and the global kpisCalcUpdated timestamp didn't move either. A short TTL avoids
    // repeatedly re-fetching multi-megabyte payloads (the global KPI screener alone is ~680KB-4.5MB)
    // within that same trading day, while staying far short of the ~24h window in which the
    // underlying data could plausibly change.
    private static readonly TimeSpan MarketDataCacheTtl = TimeSpan.FromHours(1);

    // Guards the cache-miss path only (cache hits never touch this): IMemoryCache.GetOrCreateAsync
    // does not serialize concurrent misses on the same key, so two tool calls racing on a cold cache
    // (e.g. an MCP client firing several searches at once) would otherwise both hit the live API.
    // Shared by both TTL tiers above — a single semaphore is fine here: even with the market-data
    // tier's more frequent (hourly, and higher-cardinality per kpiId/calcGroup/calc) misses,
    // contention only serializes the fetch itself (milliseconds), never blocks a cache hit, and two
    // unrelated misses briefly queuing behind each other is not something a single-user session
    // would notice.
    // Must be static, not an instance field: AddHttpClient<BorsdataApiClient>() registers this class
    // as transient (a new instance per resolution, even though the underlying HttpMessageHandler is
    // pooled), so an instance-level lock would not actually be shared across concurrent tool calls —
    // the same class of bug RateLimitHandler already had to be made a Singleton to avoid.
    private static readonly SemaphoreSlim CacheLock = new(1, 1);

    public Task<JsonNode?> GetInstrumentsAsync(CancellationToken ct = default) =>
        GetCachedAsync("instruments", ct);

    // Same envelope shape as GetInstrumentsAsync but for Börsdata's global (non-Nordic, Pro+)
    // instrument universe — confirmed live: 16,129 entries, 4.57MB, over 30x GetInstrumentsAsync's
    // ~1,700/145KB. Cached under its own key so a caller that never asks for global data never
    // pays this endpoint's cost.
    public Task<JsonNode?> GetGlobalInstrumentsAsync(CancellationToken ct = default) =>
        GetCachedAsync("instruments/global", ct);

    public Task<JsonNode?> GetMarketsAsync(CancellationToken ct = default) =>
        GetCachedAsync("markets", ct);

    public Task<JsonNode?> GetBranchesAsync(CancellationToken ct = default) =>
        GetCachedAsync("branches", ct);

    public Task<JsonNode?> GetSectorsAsync(CancellationToken ct = default) =>
        GetCachedAsync("sectors", ct);

    public Task<JsonNode?> GetCountriesAsync(CancellationToken ct = default) =>
        GetCachedAsync("countries", ct);

    public Task<JsonNode?> GetKpiMetadataAsync(CancellationToken ct = default) =>
        GetCachedAsync("instruments/kpis/metadata", ct);

    // Small, rarely-changing dataset (44 entries live, ~4KB) covering every instrument's stock
    // split history — cached like the other reference data. Confirmed live: this endpoint uses
    // "instrumentId" as the field name, not the "insId" every other endpoint uses.
    public Task<JsonNode?> GetStockSplitsAsync(CancellationToken ct = default) =>
        GetCachedAsync("instruments/stocksplits", ct);

    // Metadata about GetReports'/GetKpiSummary's own field names (e.g. "cash_And_Equivalents" ->
    // "Cash and equivalents", format "MCURR") — small and rarely-changing (37 entries live, ~4KB),
    // cached like the other reference data.
    public Task<JsonNode?> GetReportMetadataAsync(CancellationToken ct = default) =>
        GetCachedAsync("instruments/reports/metadata", ct);

    // Not under the "instruments/" prefix like every other reference-data endpoint — confirmed
    // live the path is just "translationmetadata". Small (138 entries, ~12KB), cached.
    public Task<JsonNode?> GetTranslationMetadataAsync(CancellationToken ct = default) =>
        GetCachedAsync("translationmetadata", ct);

    // Last-updated timestamps for recently-updated instruments (confirmed live: 700 entries,
    // ~37KB) — NOT every instrument (confirmed live: Volvo B, insId 236, is absent entirely despite
    // being an active, well-known instrument), so this is closer to "recently touched" than a
    // complete per-instrument index. Deliberately NOT cached like the other reference-data
    // endpoints — caching "when was this last updated" data would make it lie about freshness,
    // defeating its entire purpose.
    public Task<JsonNode?> GetInstrumentsUpdatedAsync(CancellationToken ct = default) =>
        GetAsync("instruments/updated", ct);

    // Confirmed live: a single global timestamp ({"kpisCalcUpdated": "2026-09-12T06:14:52.413"}),
    // not a per-instrument list like GetInstrumentsUpdatedAsync — the simplest response shape in
    // this API. Also not cached, for the same freshness reason.
    public Task<JsonNode?> GetKpisUpdatedAsync(CancellationToken ct = default) =>
        GetAsync("instruments/kpis/updated", ct);

    // Per the official Swagger spec (https://apidoc.borsdata.se/swagger/index.html — checked
    // live, not just the wiki, which can be stale), this endpoint's own "maxCount" is "Max Year
    // Count. Max 20" — a lookback-window size in years, not a row/entry limit. Confirmed live
    // (2026-09-12, instrumentId 352): omitting maxCount defaults to 10 years (2,518 entries);
    // maxCount=1/5 return ~1/~5 years (250/1,257 entries); maxCount=0 returns nothing;
    // maxCount>=20 (including out-of-range values like 200) clamps to the full 20-year cap
    // (4,960 entries — this instrument's entire history) rather than erroring. from/to, when
    // given, define the window directly and maxCount has no additional effect on top of them
    // (confirmed live: from=2026-08-01 with maxCount=1 still returned exactly the from-bounded 30
    // entries, not a 1-year window) — so maxCount only matters when from/to are omitted. This is
    // a plain pass-through like GetKpiHistoryAsync/GetKpiSummaryAsync; there's no client-side
    // capping here (an earlier version of this method assumed, incorrectly, that maxCount did
    // nothing at all) — see MarketDataTools.GetStockPrices for the tool-facing years semantics.
    public Task<JsonNode?> GetStockPricesAsync(
        int instrumentId, DateOnly? from = null, DateOnly? to = null, int? maxCount = null, CancellationToken ct = default)
    {
        var query = BuildQuery(
            ("from", from?.ToString("yyyy-MM-dd")),
            ("to", to?.ToString("yyyy-MM-dd")),
            ("maxCount", maxCount?.ToString()));
        return GetAsync($"instruments/{instrumentId}/stockprices{query}", ct);
    }

    // Latest daily price for every instrument in one call (~1,700 entries live, ~145KB). Cached
    // with the short MarketDataCacheTtl (see its declaration above) rather than left unfetched —
    // confirmed live this only changes once per trading day, not continuously.
    public Task<JsonNode?> GetLatestStockPricesAsync(CancellationToken ct = default) =>
        GetCachedAsync("instruments/stockprices/last", MarketDataCacheTtl, ct);

    // Global counterpart to GetLatestStockPricesAsync — confirmed live: 16,129 entries, 1.35MB.
    // Same MarketDataCacheTtl caching as the Nordic version, for the same reason.
    public Task<JsonNode?> GetGlobalLatestStockPricesAsync(CancellationToken ct = default) =>
        GetCachedAsync("instruments/stockprices/global/last", MarketDataCacheTtl, ct);

    // Same shape/size as GetLatestStockPricesAsync, but for a specific historical date. Confirmed
    // live: a non-trading day (weekend/holiday) returns an empty list rather than an error, and
    // passing instList has no effect server-side — every instrument comes back regardless, same as
    // GetLatestStockPricesAsync, so client-side filtering is required (see MarketDataTools). Cached
    // per-date (the date is part of the cache key via the query string) with MarketDataCacheTtl —
    // a past date's data never changes, but a uniform short TTL is simpler than special-casing
    // "already-elapsed" vs. "today's" date, and still cuts out most repeat fetches within a session.
    public Task<JsonNode?> GetStockPricesByDateAsync(DateOnly date, CancellationToken ct = default)
    {
        var query = BuildQuery(("date", date.ToString("yyyy-MM-dd")));
        return GetCachedAsync($"instruments/stockprices/date{query}", MarketDataCacheTtl, ct);
    }

    // Global counterpart to GetStockPricesByDateAsync — same per-date MarketDataCacheTtl caching.
    public Task<JsonNode?> GetGlobalStockPricesByDateAsync(DateOnly date, CancellationToken ct = default)
    {
        var query = BuildQuery(("date", date.ToString("yyyy-MM-dd")));
        return GetCachedAsync($"instruments/stockprices/global/date{query}", MarketDataCacheTtl, ct);
    }

    // Named to match Börsdata's own terminology for this endpoint ("KPI Screener" per their API
    // wiki) rather than "summary" — Börsdata has a separate, unrelated /kpis/{reportType}/summary
    // endpoint (see GetKpiSummaryAsync below) that the old name collided with.
    public Task<JsonNode?> GetKpiScreenerAsync(int instrumentId, int kpiId, string calcGroup, string calc, CancellationToken ct = default) =>
        GetAsync($"instruments/{instrumentId}/kpis/{kpiId}/{calcGroup}/{calc}", ct);

    public Task<JsonNode?> GetKpiHistoryAsync(
        int instrumentId, int kpiId, string reportType, string priceType, int? maxCount = null, CancellationToken ct = default)
    {
        var query = BuildQuery(("maxCount", maxCount?.ToString()));
        return GetAsync($"instruments/{instrumentId}/kpis/{kpiId}/{reportType}/{priceType}/history{query}", ct);
    }

    // Returns every KPI for one instrument across multiple periods in one call (confirmed live:
    // 42 KPIs for Volvo B) — the actual "KPI summary" endpoint per Börsdata's own naming, unlike
    // GetKpiScreenerAsync above (a single value for one specific KPI). Confirmed live: maxCount
    // already caps the number of periods per KPI server-side (27 -> 2 periods), so this is a plain
    // pass-through like GetKpiHistoryAsync.
    public Task<JsonNode?> GetKpiSummaryAsync(int instrumentId, string reportType, int? maxCount = null, CancellationToken ct = default)
    {
        var query = BuildQuery(("maxCount", maxCount?.ToString()));
        return GetAsync($"instruments/{instrumentId}/kpis/{reportType}/summary{query}", ct);
    }

    // Same KPI screener calculation as GetKpiScreenerAsync, but for every instrument at once
    // (~14,000 entries live) instead of one. Cached per kpiId/calcGroup/calc combination (part of
    // the cache key) with the short MarketDataCacheTtl — confirmed live these values (e.g.
    // price-derived KPIs like P/E) only change once per trading day, not continuously.
    public Task<JsonNode?> GetKpiListScreenerAsync(int kpiId, string calcGroup, string calc, CancellationToken ct = default) =>
        GetCachedAsync($"instruments/kpis/{kpiId}/{calcGroup}/{calc}", MarketDataCacheTtl, ct);

    // Global counterpart to GetKpiListScreenerAsync — confirmed live: 16,129 entries, 681KB. Note
    // "global" sits right after "instruments/" here, not appended like the two stockprices
    // endpoints above — matches Börsdata's own URL layout, not a typo. Same per-combination
    // MarketDataCacheTtl caching as the Nordic version.
    public Task<JsonNode?> GetGlobalKpiListScreenerAsync(int kpiId, string calcGroup, string calc, CancellationToken ct = default) =>
        GetCachedAsync($"instruments/global/kpis/{kpiId}/{calcGroup}/{calc}", MarketDataCacheTtl, ct);

    public Task<JsonNode?> GetReportsAsync(int instrumentId, string reportType, CancellationToken ct = default) =>
        GetAsync($"instruments/{instrumentId}/reports/{reportType}", ct);

    // Returns each instrument's full report-date history (past and scheduled future dates) —
    // confirmed live there's no server-side maxCount for this endpoint, unlike stockprices/kpi
    // history, so any capping has to happen client-side (see CalendarTools).
    public Task<JsonNode?> GetReportCalendarAsync(string instrumentIds, CancellationToken ct = default)
    {
        var query = BuildQuery(("instList", instrumentIds));
        return GetAsync($"instruments/report/calendar{query}", ct);
    }

    // Same shape/caveats as GetReportCalendarAsync: full past+future history, no server-side maxCount.
    public Task<JsonNode?> GetDividendCalendarAsync(string instrumentIds, CancellationToken ct = default)
    {
        var query = BuildQuery(("instList", instrumentIds));
        return GetAsync($"instruments/dividend/calendar{query}", ct);
    }

    // Returns each requested instrument's entire transaction history (hundreds of entries for an
    // old company, confirmed live for Volvo B), oldest first, no server-side date/amount filtering
    // or limiting — see HoldingsTools for client-side filtering/sorting/capping.
    public Task<JsonNode?> GetInsiderHoldingsAsync(string instrumentIds, CancellationToken ct = default)
    {
        var query = BuildQuery(("instList", instrumentIds));
        return GetAsync($"holdings/insider{query}", ct);
    }

    // Same shape/caveats as GetInsiderHoldingsAsync: full past history oldest-first, no server-side
    // date filtering (confirmed live a maxCount query param has no effect either — 140 entries for
    // Volvo B regardless), so filtering/sorting/capping happens client-side (see HoldingsTools).
    public Task<JsonNode?> GetBuybackHoldingsAsync(string instrumentIds, CancellationToken ct = default)
    {
        var query = BuildQuery(("instList", instrumentIds));
        return GetAsync($"holdings/buyback{query}", ct);
    }

    // Unlike GetInsiderHoldingsAsync, this endpoint doesn't accept an instList — it always returns
    // every Nordic instrument (~400+ entries live) in one call. Not cached: shorting percentages
    // change daily.
    public Task<JsonNode?> GetShortHoldingsAsync(CancellationToken ct = default) =>
        GetAsync("holdings/shorts", ct);

    private Task<JsonNode?> GetCachedAsync(string endpoint, CancellationToken ct) =>
        GetCachedAsync(endpoint, ReferenceDataCacheTtl, ct);

    private async Task<JsonNode?> GetCachedAsync(string endpoint, TimeSpan ttl, CancellationToken ct)
    {
        if (cache.TryGetValue(endpoint, out JsonNode? cached))
            return cached;

        await CacheLock.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(endpoint, out cached))
                return cached;

            var result = await GetAsync(endpoint, ct);
            cache.Set(endpoint, result, ttl);
            return result;
        }
        finally
        {
            CacheLock.Release();
        }
    }

    private async Task<JsonNode?> GetAsync(string path, CancellationToken ct)
    {
        using var response = await httpClient.GetAsync(path, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct);
    }

    private static string BuildQuery(params (string Key, string? Value)[] parameters)
    {
        var pairs = parameters
            .Where(p => p.Value is not null)
            .Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}")
            .ToArray();
        return pairs.Length == 0 ? string.Empty : "?" + string.Join('&', pairs);
    }
}
