using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace BorsdataMcp.Tools;

[McpServerToolType]
public static class HoldingsTools
{
    [McpServerTool, Description(
        "Gets insider transactions (board members/executives trading their own company's shares) " +
        "for specified instruments. Börsdata returns each instrument's entire transaction history " +
        "(hundreds of entries for an old company) oldest first, with no server-side date/amount " +
        "filtering or limiting — results here are sorted most-recent-first and filtered/capped " +
        "client-side. Börsdata's raw transactionType codes aren't documented and don't match a " +
        "simple buy/sell scheme, so use 'direction' (based on the sign of the shares field) instead " +
        "to distinguish acquisitions from disposals. Returns one entry per instrument: " +
        "{ insId, totalMatched, returned, transactions }. Also works transparently for a global " +
        "(non-Nordic, Pro+) instrument's insId — discover one via ListInstruments with " +
        "includeGlobal:true — but Börsdata does not track insider disclosures for most global " +
        "instruments, so a global insId typically returns an empty result (HTTP 200, not an error) " +
        "rather than failing.")]
    public static async Task<string> GetInsiderHoldings(
        BorsdataApiClient client,
        [Description("Comma-separated instrument insIds, from ListInstruments.")] string instrumentIds,
        [Description("Only include transactions on or after this date, 'yyyy-MM-dd'. Optional.")]
        string? fromDate = null,
        [Description("Only include transactions on or before this date, 'yyyy-MM-dd'. Optional.")]
        string? toDate = null,
        [Description("Only include transactions where the absolute amount is at least this value. Optional.")]
        double? minAmount = null,
        [Description("Filter by direction: 'increase' (shares > 0 — a purchase or equity grant) or 'decrease' (shares < 0 — a sale). Optional.")]
        string? direction = null,
        [Description("Maximum number of transactions to return per instrument, most recent first. Omit to return all matches.")]
        int? maxCount = null,
        CancellationToken cancellationToken = default)
    {
        var raw = await client.GetInsiderHoldingsAsync(instrumentIds, cancellationToken);
        bool ExtraFilter(JsonObject e) => MatchesAmountAndDirection(e, minAmount, direction);
        return BuildRecentFirstResult(raw, "transactionDate", "transactions", fromDate, toDate, maxCount, ExtraFilter).ToJsonString();
    }

    [McpServerTool, Description(
        "Gets share buyback transactions (a company repurchasing its own shares) for specified " +
        "instruments. Börsdata returns each instrument's entire buyback history oldest first, with " +
        "no server-side date filtering or limiting — results here are sorted most-recent-first and " +
        "filtered/capped client-side. Returns one entry per instrument: " +
        "{ insId, totalMatched, returned, buybacks }. Also works transparently for a global " +
        "(non-Nordic, Pro+) instrument's insId — discover one via ListInstruments with " +
        "includeGlobal:true — but Börsdata does not track buyback disclosures for most global " +
        "instruments, so a global insId typically returns an empty result (HTTP 200, not an error) " +
        "rather than failing.")]
    public static async Task<string> GetBuybackHoldings(
        BorsdataApiClient client,
        [Description("Comma-separated instrument insIds, from ListInstruments.")] string instrumentIds,
        [Description("Only include buybacks on or after this date, 'yyyy-MM-dd'. Optional.")]
        string? fromDate = null,
        [Description("Only include buybacks on or before this date, 'yyyy-MM-dd'. Optional.")]
        string? toDate = null,
        [Description("Maximum number of buybacks to return per instrument, most recent first. Omit to return all matches.")]
        int? maxCount = null,
        CancellationToken cancellationToken = default)
    {
        var raw = await client.GetBuybackHoldingsAsync(instrumentIds, cancellationToken);
        return BuildRecentFirstResult(raw, "date", "buybacks", fromDate, toDate, maxCount).ToJsonString();
    }

    [McpServerTool, Description(
        "Gets short-position data (shorting %, holder count, days-to-cover, trend) for instruments — " +
        "Börsdata returns this for every Nordic instrument in one call (~400+ entries), with no " +
        "server-side per-instrument filtering, so use instrumentIds to restrict to specific " +
        "instruments (e.g. your holdings) or minShortingPercent/maxCount to narrow a market-wide " +
        "screen. Sorted by shorting percent descending by default (most-shorted first). Each result " +
        "is enriched with ticker/name. Returns { totalMatched, returned, values }. Covers Nordic " +
        "instruments only — Börsdata has no global counterpart for this endpoint.")]
    public static async Task<string> GetShortHoldings(
        BorsdataApiClient client,
        [Description("Comma-separated instrument insIds to restrict results to, e.g. your holdings resolved via ListInstruments. Optional — omit to screen all instruments.")]
        string? instrumentIds = null,
        [Description("Only include instruments whose shorting percent (absolute value) is at least this. Optional.")]
        double? minShortingPercent = null,
        [Description("Sort ascending (least-shorted first) instead of the default descending (most-shorted first).")]
        bool sortAscending = false,
        [Description("Maximum number of results to return. Omit to return all matches.")]
        int? maxCount = null,
        CancellationToken cancellationToken = default)
    {
        var raw = await client.GetShortHoldingsAsync(cancellationToken);
        var instruments = await client.GetInstrumentsAsync(cancellationToken);
        return BuildShortHoldingsResult(raw, instruments, instrumentIds, minShortingPercent, sortAscending, maxCount).ToJsonString();
    }

    // Shared by GetInsiderHoldings/GetBuybackHoldings: both endpoints return
    // { "list": [ { "insId", "values": [ { <dateField>: .., ... }, ... ] } ] } with each
    // instrument's entire history, oldest first, and no server-side date filtering or limiting.
    // "Recent activity" is the overwhelmingly common need for both, so results are always
    // re-sorted most-recent-first (unlike CalendarTools' report/dividend calendars, which sort
    // ascending because "next N upcoming" is the natural query there instead).
    private static JsonObject BuildRecentFirstResult(
        JsonNode? root, string dateField, string resultsKey, string? fromDate, string? toDate, int? maxCount,
        Func<JsonObject, bool>? extraFilter = null)
    {
        var list = (root as JsonObject)?["list"] as JsonArray ?? [];
        var from = fromDate is null ? (DateOnly?)null : DateOnly.Parse(fromDate);
        var to = toDate is null ? (DateOnly?)null : DateOnly.Parse(toDate);

        var instruments = new JsonArray();
        foreach (var node in list.OfType<JsonObject>())
        {
            if (node["insId"] is not JsonValue idValue || !idValue.TryGetValue(out int insId))
                continue;

            var entries = (node["values"] as JsonArray)?.OfType<JsonObject>() ?? [];
            var filtered = entries
                .Where(e => IsWithinDateRange(e, dateField, from, to) && (extraFilter is null || extraFilter(e)))
                .ToList();

            filtered.Sort((a, b) => string.CompareOrdinal(GetString(b, dateField), GetString(a, dateField)));

            var take = maxCount ?? filtered.Count;
            var page = new JsonArray(filtered.Take(take).Select(e => (JsonNode)e.DeepClone()).ToArray());

            instruments.Add((JsonNode)new JsonObject
            {
                ["insId"] = insId,
                ["totalMatched"] = filtered.Count,
                ["returned"] = page.Count,
                [resultsKey] = page
            });
        }

        return new JsonObject { ["instruments"] = instruments };
    }

    private static bool IsWithinDateRange(JsonObject entry, string dateField, DateOnly? from, DateOnly? to)
    {
        if (from is null && to is null)
            return true;

        var dateStr = GetString(entry, dateField);
        if (dateStr.Length == 0 || !DateOnly.TryParse(dateStr.Split('T')[0], out var date))
            return true;

        return (from is null || date >= from) && (to is null || date <= to);
    }

    private static bool MatchesAmountAndDirection(JsonObject entry, double? minAmount, string? direction)
    {
        if (minAmount is not null)
        {
            var amount = entry["amount"] is JsonValue av && av.TryGetValue(out double a) ? Math.Abs(a) : 0;
            if (amount < minAmount) return false;
        }

        if (!string.IsNullOrWhiteSpace(direction))
        {
            var shares = entry["shares"] is JsonValue sv && sv.TryGetValue(out double s) ? s : 0;
            if (string.Equals(direction, "increase", StringComparison.OrdinalIgnoreCase) && shares <= 0) return false;
            if (string.Equals(direction, "decrease", StringComparison.OrdinalIgnoreCase) && shares >= 0) return false;
        }

        return true;
    }

    private static string GetString(JsonObject entry, string field) =>
        entry[field] is JsonValue v && v.TryGetValue(out string? s) ? s : "";

    private static JsonObject BuildShortHoldingsResult(
        JsonNode? root, JsonNode? instrumentsRoot, string? instrumentIds, double? minShortingPercent, bool sortAscending, int? maxCount)
    {
        var list = (root as JsonObject)?["list"] as JsonArray ?? [];
        var instrumentIndex = InstrumentLookup.BuildIndex(instrumentsRoot);
        var idFilter = InstrumentLookup.ParseIds(instrumentIds);

        var entries = new List<(int InsId, double? ShortsProc, JsonObject Raw)>();
        foreach (var node in list.OfType<JsonObject>())
        {
            if (node["insId"] is not JsonValue idValue || !idValue.TryGetValue(out int insId))
                continue;
            if (idFilter is not null && !idFilter.Contains(insId))
                continue;

            // Börsdata stores shortsProc as a negative number for display reasons — magnitude is
            // what matters for "how heavily shorted," confirmed live (e.g. -7.69 for a 7.69% short).
            double? shortsProc = node["shortsProc"] is JsonValue spv && spv.TryGetValue(out double sp) ? sp : null;

            if (minShortingPercent is not null && (shortsProc is null || Math.Abs(shortsProc.Value) < minShortingPercent))
                continue;

            entries.Add((insId, shortsProc, node));
        }

        // Entries without a shortsProc value sort last regardless of direction.
        var ordered = sortAscending
            ? entries.OrderBy(e => e.ShortsProc is null).ThenBy(e => e.ShortsProc.HasValue ? Math.Abs(e.ShortsProc.Value) : 0)
            : entries.OrderBy(e => e.ShortsProc is null).ThenByDescending(e => e.ShortsProc.HasValue ? Math.Abs(e.ShortsProc.Value) : 0);
        var matched = ordered.ToList();
        var take = maxCount ?? matched.Count;

        var page = matched.Take(take).Select(e =>
        {
            var obj = (JsonObject)e.Raw.DeepClone();
            if (instrumentIndex.TryGetValue(e.InsId, out var info))
            {
                obj["ticker"] = info.Ticker;
                obj["name"] = info.Name;
            }
            return (JsonNode)obj;
        }).ToArray();

        return new JsonObject
        {
            ["totalMatched"] = matched.Count,
            ["returned"] = page.Length,
            ["values"] = new JsonArray(page)
        };
    }
}
