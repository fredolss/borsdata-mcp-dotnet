using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace BorsdataMcp.Tools;

[McpServerToolType]
public static class MarketDataTools
{
    [McpServerTool, Description(
        "Gets daily stock price history (open, high, low, close, volume) for one instrument. " +
        "Omit from/to/maxCount for the API's default window (10 years). maxCount is a lookback " +
        "window in YEARS (1-20, per Börsdata's own limit for this endpoint), not a count of " +
        "days/entries — pass from/to instead for an exact date range or a small recent window " +
        "(e.g. the last 30 days). Also works transparently for a global (non-Nordic, Pro+) " +
        "instrument's insId — discover one via list_instruments with includeGlobal:true.")]
    public static async Task<string> GetStockPrices(
        BorsdataApiClient client,
        [Description("The instrument's insId, from list_instruments.")] int instrumentId,
        [Description("Start date, 'yyyy-MM-dd'. Optional.")] string? from = null,
        [Description("End date, 'yyyy-MM-dd'. Optional.")] string? to = null,
        [Description("Lookback window in years (1-20), applied server-side by Börsdata. Only has an effect when from is omitted — has no effect on top of an explicit from/to range. Optional; omitting it defaults to 10 years.")]
        int? maxCount = null,
        CancellationToken cancellationToken = default) =>
        (await client.GetStockPricesAsync(
            instrumentId,
            from is null ? null : DateOnly.Parse(from),
            to is null ? null : DateOnly.Parse(to),
            maxCount,
            cancellationToken))?.ToJsonString() ?? "{}";

    [McpServerTool, Description(
        "Gets the latest daily stock price (open, high, low, close, volume) for every instrument on " +
        "Börsdata in one call — Börsdata returns ~1,700 entries unfiltered, so prefer instrumentIds " +
        "(e.g. your holdings from list_instruments) and/or maxCount to keep the response small. Each " +
        "result is enriched with ticker/name. Returns { totalMatched, returned, values }.")]
    public static async Task<string> GetLatestStockPrices(
        BorsdataApiClient client,
        [Description("Comma-separated instrument insIds to restrict results to, e.g. your holdings from list_instruments. Optional — omit to get every instrument.")]
        string? instrumentIds = null,
        [Description("Query Börsdata's global (non-Nordic, Pro+) instrument universe instead of " +
            "the default Nordic one. Switches the data source rather than merging it — unlike " +
            "list_instruments' includeGlobal, since this endpoint isn't cached and merging by " +
            "default would double live API traffic and payload size on every call. Default false.")]
        bool global = false,
        [Description("Maximum number of results to return. Omit to return all matches.")]
        int? maxCount = null,
        CancellationToken cancellationToken = default)
    {
        var raw = global
            ? await client.GetGlobalLatestStockPricesAsync(cancellationToken)
            : await client.GetLatestStockPricesAsync(cancellationToken);
        var instruments = global
            ? await client.GetGlobalInstrumentsAsync(cancellationToken)
            : await client.GetInstrumentsAsync(cancellationToken);
        return BuildStockPricesResult(raw, instruments, instrumentIds, maxCount).ToJsonString();
    }

    [McpServerTool, Description(
        "Gets each instrument's stock price (open, high, low, close, volume) on a specific " +
        "historical date — the same data as get_latest_stock_prices but for a date you choose instead " +
        "of the most recent trading day. A weekend/holiday date returns no results rather than an " +
        "error. Börsdata returns ~1,700 entries unfiltered, so prefer instrumentIds and/or maxCount " +
        "to keep the response small. Each result is enriched with ticker/name. Returns " +
        "{ totalMatched, returned, values }.")]
    public static async Task<string> GetStockPricesByDate(
        BorsdataApiClient client,
        [Description("The date to get prices for, 'yyyy-MM-dd'.")] string date,
        [Description("Comma-separated instrument insIds to restrict results to, e.g. your holdings from list_instruments. Optional — omit to get every instrument.")]
        string? instrumentIds = null,
        [Description("Query Börsdata's global (non-Nordic, Pro+) instrument universe instead of " +
            "the default Nordic one. Switches the data source rather than merging it — unlike " +
            "list_instruments' includeGlobal, since this endpoint isn't cached and merging by " +
            "default would double live API traffic and payload size on every call. Default false.")]
        bool global = false,
        [Description("Maximum number of results to return. Omit to return all matches.")]
        int? maxCount = null,
        CancellationToken cancellationToken = default)
    {
        var raw = global
            ? await client.GetGlobalStockPricesByDateAsync(DateOnly.Parse(date), cancellationToken)
            : await client.GetStockPricesByDateAsync(DateOnly.Parse(date), cancellationToken);
        var instruments = global
            ? await client.GetGlobalInstrumentsAsync(cancellationToken)
            : await client.GetInstrumentsAsync(cancellationToken);
        return BuildStockPricesResult(raw, instruments, instrumentIds, maxCount).ToJsonString();
    }

    private static JsonObject BuildStockPricesResult(JsonNode? pricesRoot, JsonNode? instrumentsRoot, string? instrumentIds, int? maxCount)
    {
        var rawValues = (pricesRoot as JsonObject)?["stockPricesList"] as JsonArray ?? [];
        var instrumentIndex = InstrumentLookup.BuildIndex(instrumentsRoot);
        var idFilter = InstrumentLookup.ParseIds(instrumentIds);

        var matched = new List<(int InsId, JsonObject Raw)>();
        foreach (var node in rawValues.OfType<JsonObject>())
        {
            if (node["i"] is not JsonValue idValue || !idValue.TryGetValue(out int insId))
                continue;
            if (idFilter is not null && !idFilter.Contains(insId))
                continue;

            matched.Add((insId, node));
        }

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

    [McpServerTool, Description("Gets a calculated KPI value (e.g. P/E, revenue growth) for one instrument. kpiId/calcGroup/calc identify the specific metric per the Börsdata KPI reference (https://borsdata.se/en/insights/api). For every KPI at once for this instrument, use get_kpi_summary instead. For shorting/short-interest data specifically, use get_short_holdings instead — it doesn't require guessing a kpiId/calcGroup/calc combination. Also works transparently for a global (non-Nordic, Pro+) instrument's insId — discover one via list_instruments with includeGlobal:true.")]
    public static async Task<string> GetKpiScreener(
        BorsdataApiClient client,
        [Description("The instrument's insId, from list_instruments.")] int instrumentId,
        [Description("The Börsdata KPI id.")] int kpiId,
        [Description("The calculation group, e.g. 'last', 'quarter', 'year'.")] string calcGroup,
        [Description("The calculation, e.g. 'latest', 'cagr5y', 'mean'.")] string calc,
        CancellationToken cancellationToken) =>
        (await client.GetKpiScreenerAsync(instrumentId, kpiId, calcGroup, calc, cancellationToken))?.ToJsonString() ?? "{}";

    [McpServerTool, Description("Gets historical values for a KPI (e.g. P/E) over time for one instrument — how the metric has trended across periods, unlike get_kpi_screener which returns a single current value. kpiId identifies the metric (see list_kpi_metadata); reportType/priceType follow the Börsdata KPI reference (https://borsdata.se/en/insights/api) and not every combination is valid for every KPI (an invalid one returns an HTTP 400 from Börsdata). Omit maxCount for the API's default window. Also works transparently for a global (non-Nordic, Pro+) instrument's insId — discover one via list_instruments with includeGlobal:true.")]
    public static async Task<string> GetKpiHistory(
        BorsdataApiClient client,
        [Description("The instrument's insId, from list_instruments.")] int instrumentId,
        [Description("The Börsdata KPI id, from list_kpi_metadata (e.g. 2 for P/E).")] int kpiId,
        [Description("The report period, e.g. 'year' or 'r12'.")] string reportType,
        [Description("The price/value basis, e.g. 'mean', 'high', 'low', 'latest'.")] string priceType,
        [Description("Maximum number of most recent periods to return. Optional.")] int? maxCount = null,
        CancellationToken cancellationToken = default) =>
        (await client.GetKpiHistoryAsync(instrumentId, kpiId, reportType, priceType, maxCount, cancellationToken))?.ToJsonString() ?? "{}";

    [McpServerTool, Description("Gets every KPI Börsdata tracks (P/E, revenue growth, margins, etc.) for one instrument across multiple periods in one call — unlike get_kpi_screener, which returns a single value for one specific KPI. Each entry is keyed by KpiId (see list_kpi_metadata to resolve names); omit maxCount for the API's default number of periods per KPI. Also works transparently for a global (non-Nordic, Pro+) instrument's insId — discover one via list_instruments with includeGlobal:true.")]
    public static async Task<string> GetKpiSummary(
        BorsdataApiClient client,
        [Description("The instrument's insId, from list_instruments.")] int instrumentId,
        [Description("Report period: 'year', 'quarter', or 'r12'.")] string reportType,
        [Description("Maximum number of most recent periods to return per KPI. Optional.")] int? maxCount = null,
        CancellationToken cancellationToken = default) =>
        (await client.GetKpiSummaryAsync(instrumentId, reportType, maxCount, cancellationToken))?.ToJsonString() ?? "{}";

    [McpServerTool, Description("Gets financial reports (income statement, balance sheet, cash flow) for one instrument. Also works transparently for a global (non-Nordic, Pro+) instrument's insId — discover one via list_instruments with includeGlobal:true.")]
    public static async Task<string> GetReports(
        BorsdataApiClient client,
        [Description("The instrument's insId, from list_instruments.")] int instrumentId,
        [Description("Report period: 'year', 'quarter', or 'r12'.")] string reportType,
        CancellationToken cancellationToken) =>
        (await client.GetReportsAsync(instrumentId, reportType, cancellationToken))?.ToJsonString() ?? "{}";

    [McpServerTool, Description(
        "Gets a calculated KPI value (e.g. P/E) for every instrument on Börsdata in one call — the " +
        "KPI screener across the whole market. Useful for screening (e.g. \"which companies have " +
        "P/E under 15\") or for checking one KPI across a specific set of holdings. kpiId/calcGroup/" +
        "calc identify the metric per the Börsdata KPI reference (https://borsdata.se/en/insights/api). " +
        "This endpoint has no server-side market/sector/country filter — if the request is scoped to " +
        "a specific market/sector/country/branch (e.g. \"Swedish large cap\"), first call list_instruments " +
        "with that filter (e.g. marketId) to resolve the matching insIds, then pass them as " +
        "instrumentIds here; without that, this screens Börsdata's *entire* universe and the result " +
        "will include instruments outside the requested scope. Without instrumentIds or value bounds " +
        "this covers roughly 14,000 instruments — prefer instrumentIds and/or minValue/maxValue/" +
        "maxCount to keep the response small. Each result includes ticker/name alongside insId so a " +
        "second lookup isn't needed. For shorting/short-interest data specifically, use " +
        "get_short_holdings instead — it doesn't require guessing a kpiId/calcGroup/calc combination.")]
    public static async Task<string> GetKpiListScreener(
        BorsdataApiClient client,
        [Description("The Börsdata KPI id, e.g. 2 for P/E.")] int kpiId,
        [Description("The calculation group, e.g. 'last', 'quarter', 'year', '1year', '3year'.")] string calcGroup,
        [Description("The calculation, e.g. 'latest', 'mean', 'high', 'low'.")] string calc,
        [Description("Comma-separated instrument insIds to restrict results to — e.g. a set of holdings, or " +
            "the insIds from a list_instruments call filtered by marketId/countryId/sectorId/branchId when " +
            "the request is scoped to a specific market/sector/country (pass those ids here, don't skip " +
            "this step). Optional — omit only to intentionally screen every instrument.")]
        string? instrumentIds = null,
        [Description("Only include instruments whose value is greater than or equal to this. Optional.")]
        double? minValue = null,
        [Description("Only include instruments whose value is less than or equal to this. Optional.")]
        double? maxValue = null,
        [Description("Sort by value descending instead of the default ascending. Default false.")]
        bool sortDescending = false,
        [Description("Query Börsdata's global (non-Nordic, Pro+) instrument universe instead of " +
            "the default Nordic one. Switches the data source rather than merging it — unlike " +
            "list_instruments' includeGlobal, since this endpoint isn't cached and merging by " +
            "default would double live API traffic and payload size on every call. Default false.")]
        bool global = false,
        [Description("Maximum number of results to return. Omit to return all matches.")]
        int? maxCount = null,
        CancellationToken cancellationToken = default)
    {
        var values = global
            ? await client.GetGlobalKpiListScreenerAsync(kpiId, calcGroup, calc, cancellationToken)
            : await client.GetKpiListScreenerAsync(kpiId, calcGroup, calc, cancellationToken);
        var instruments = global
            ? await client.GetGlobalInstrumentsAsync(cancellationToken)
            : await client.GetInstrumentsAsync(cancellationToken);
        return BuildKpiListScreenerResult(
            values, instruments, kpiId, calcGroup, calc, instrumentIds, minValue, maxValue, sortDescending, maxCount).ToJsonString();
    }

    private static JsonObject BuildKpiListScreenerResult(
        JsonNode? valuesRoot, JsonNode? instrumentsRoot, int kpiId, string calcGroup, string calc,
        string? instrumentIds, double? minValue, double? maxValue, bool sortDescending, int? maxCount)
    {
        var rawValues = (valuesRoot as JsonObject)?["values"] as JsonArray ?? [];
        var instrumentIndex = InstrumentLookup.BuildIndex(instrumentsRoot);
        var idFilter = InstrumentLookup.ParseIds(instrumentIds);

        var entries = new List<(int InsId, double? Numeric, string? StringValue)>();
        foreach (var node in rawValues.OfType<JsonObject>())
        {
            if (node["i"] is not JsonValue idValue || !idValue.TryGetValue(out int insId))
                continue;
            if (idFilter is not null && !idFilter.Contains(insId))
                continue;

            double? numeric = node["n"] is JsonValue nv && nv.TryGetValue(out double d) ? d : null;
            string? str = node["s"] is JsonValue sv && sv.TryGetValue(out string? s) ? s : null;

            if (minValue is not null && (numeric is null || numeric < minValue))
                continue;
            if (maxValue is not null && (numeric is null || numeric > maxValue))
                continue;

            entries.Add((insId, numeric, str));
        }

        // Entries without a numeric value (string-only KPIs) sort last regardless of direction —
        // they can't be meaningfully ordered against a numeric scale.
        var ordered = sortDescending
            ? entries.OrderBy(e => e.Numeric is null).ThenByDescending(e => e.Numeric)
            : entries.OrderBy(e => e.Numeric is null).ThenBy(e => e.Numeric);
        var matched = ordered.ToList();
        var take = maxCount ?? matched.Count;

        var page = matched.Take(take).Select(e =>
        {
            var obj = new JsonObject { ["insId"] = e.InsId };
            if (instrumentIndex.TryGetValue(e.InsId, out var info))
            {
                obj["ticker"] = info.Ticker;
                obj["name"] = info.Name;
            }
            if (e.Numeric is not null)
                obj["value"] = e.Numeric;
            else if (e.StringValue is not null)
                obj["value"] = e.StringValue;
            return (JsonNode)obj;
        }).ToArray();

        return new JsonObject
        {
            ["kpiId"] = kpiId,
            ["calcGroup"] = calcGroup,
            ["calc"] = calc,
            ["totalMatched"] = matched.Count,
            ["returned"] = page.Length,
            ["values"] = new JsonArray(page)
        };
    }
}
