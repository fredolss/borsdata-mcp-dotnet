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

    [McpServerTool, Description("Gets a calculated KPI value (e.g. P/E, revenue growth) for one instrument. kpiId/calcGroup/calc identify the specific metric per the Börsdata KPI reference (https://borsdata.se/en/insights/api), and not every calcGroup/calc combination is valid for every kpiId — an invalid one returns an HTTP 400 from Börsdata (the error message now includes Börsdata's own explanation). If unsure, calcGroup 'last' with calc 'latest' is confirmed to work for most KPIs; 'year'/'latest' does NOT work for kpiId 2 (P/E), for example. For every KPI at once for this instrument, use get_kpi_summary instead. For shorting/short-interest data specifically, use get_short_holdings instead — it doesn't require guessing a kpiId/calcGroup/calc combination. Also works transparently for a global (non-Nordic, Pro+) instrument's insId — discover one via list_instruments with includeGlobal:true.")]
    public static async Task<string> GetKpiScreener(
        BorsdataApiClient client,
        [Description("The instrument's insId, from list_instruments.")] int instrumentId,
        [Description("The Börsdata KPI id.")] int kpiId,
        [Description("The calculation group. 'last' is confirmed to work broadly; 'year'/'quarter'/'1year'/'3year' " +
            "are valid for some KPIs but not others (Börsdata returns HTTP 400 for an invalid combination) — if " +
            "unsure, try 'last' first.")]
        string calcGroup,
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

    [McpServerTool, Description(
        "Gets historical values for a KPI (e.g. P/E) over time for a LIST of instruments in one " +
        "call — the bulk version of get_kpi_history. A direct mirror of Börsdata's own \"Kpi " +
        "History\" array endpoint: despite the similar-looking URL, this is a genuinely different " +
        "endpoint from get_kpi_list_screener (different Börsdata tag, different path params — " +
        "reportType/priceType here, not calcGroup/calc) and, unlike get_kpi_list_screener, this one " +
        "DOES filter server-side by instrumentIds — real API-level scoping, not something faked " +
        "client-side. kpiId identifies the metric (see list_kpi_metadata); reportType/priceType " +
        "follow the Börsdata KPI reference (https://borsdata.se/en/insights/api) and not every " +
        "combination is valid for every KPI (an invalid one returns an HTTP 400 from Börsdata). " +
        "Returns one entry per requested instrument, each with its own history array (or an error " +
        "field if that instrument's history couldn't be resolved). maxCount caps periods per " +
        "instrument (Börsdata's own limit: 20 for 'year', 40 for 'r12'/'quarter'), not the number " +
        "of instruments returned.")]
    public static async Task<string> GetKpiHistoryArray(
        BorsdataApiClient client,
        [Description("The Börsdata KPI id, from list_kpi_metadata (e.g. 2 for P/E).")] int kpiId,
        [Description("The report period, e.g. 'year' or 'r12'.")] string reportType,
        [Description("The price/value basis, e.g. 'mean', 'high', 'low', 'latest'.")] string priceType,
        [Description("Comma-separated instrument insIds, from list_instruments. Required — Börsdata's own " +
            "API requires this for this endpoint.")]
        string instrumentIds,
        [Description("Maximum number of most recent periods to return per instrument. Optional.")] int? maxCount = null,
        CancellationToken cancellationToken = default) =>
        (await client.GetKpiHistoryArrayAsync(kpiId, reportType, priceType, instrumentIds, maxCount, cancellationToken))?.ToJsonString() ?? "{}";

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
        "Gets all financial report types (year, quarter, r12) for one instrument in a single call " +
        "— unlike get_reports, which returns just one reportType per call, so this replaces three " +
        "separate get_reports calls when the user wants the full picture (e.g. \"show me all " +
        "financial reports for Volvo\"). A direct mirror of Börsdata's own \"reports compound\" " +
        "endpoint. Returns { instrument, reportsYear, reportsQuarter, reportsR12 }.")]
    public static async Task<string> GetReportsCompound(
        BorsdataApiClient client,
        [Description("The instrument's insId, from list_instruments.")] int instrumentId,
        [Description("Maximum number of yearly report periods to return. Börsdata default 10, max 20. Optional.")]
        int? maxYearCount = null,
        [Description("Maximum number of quarterly/R12 report periods to return. Börsdata default 10, max 40. Optional.")]
        int? maxR12QCount = null,
        [Description("Return figures in the instrument's original reporting currency instead of Börsdata's " +
            "converted default. Optional.")]
        bool? original = null,
        CancellationToken cancellationToken = default) =>
        (await client.GetReportsCompoundAsync(instrumentId, maxYearCount, maxR12QCount, original, cancellationToken))?.ToJsonString() ?? "{}";

    [McpServerTool, Description(
        "Gets all financial report types (year, quarter, r12) for a LIST of instruments in one call " +
        "— the bulk version of get_reports_compound, and a direct mirror of Börsdata's own " +
        "\"reports array\" endpoint, which DOES filter server-side by instrumentIds. Returns one " +
        "entry per requested instrument under reportList (or an error field if that instrument's " +
        "reports couldn't be resolved). maxYearCount/maxR12QCount/original work the same as " +
        "get_reports_compound.")]
    public static async Task<string> GetReportsArray(
        BorsdataApiClient client,
        [Description("Comma-separated instrument insIds, from list_instruments.")] string instrumentIds,
        [Description("Maximum number of yearly report periods to return per instrument. Börsdata default 10, " +
            "max 20. Optional.")]
        int? maxYearCount = null,
        [Description("Maximum number of quarterly/R12 report periods to return per instrument. Börsdata " +
            "default 10, max 40. Optional.")]
        int? maxR12QCount = null,
        [Description("Return figures in each instrument's original reporting currency instead of Börsdata's " +
            "converted default. Optional.")]
        bool? original = null,
        CancellationToken cancellationToken = default) =>
        (await client.GetReportsArrayAsync(instrumentIds, maxYearCount, maxR12QCount, original, cancellationToken))?.ToJsonString() ?? "{}";

    [McpServerTool, Description(
        "Gets a calculated KPI value (e.g. P/E) for every Nordic instrument on Börsdata in one " +
        "call — a direct, unmodified mirror of Börsdata's own \"Kpi Screener\" bulk endpoint " +
        "(kpislistv1), which takes only kpiId/calcGroup/calc and always returns every instrument " +
        "in whatever order Börsdata itself returns them, with no sort or count control (Börsdata's " +
        "endpoint has none either). Response is large — roughly 14,000 entries unfiltered. For a " +
        "specific, hand-picked set of instruments (e.g. \"compare P/E for Volvo, Ericsson, and " +
        "Nordea\"), call get_kpi_screener once per instrument instead — that's the endpoint Börsdata " +
        "actually built for scoping to specific ids. For Börsdata's global (non-Nordic, Pro+) " +
        "instrument universe, use get_global_kpi_list_screener instead — a separate tool mirroring " +
        "Börsdata's own separate endpoint for that, not a parameter on this one. kpiId/calcGroup/" +
        "calc identify the metric per the Börsdata KPI reference " +
        "(https://borsdata.se/en/insights/api). Each result includes ticker/name alongside insId. " +
        "For shorting/short-interest data specifically, use get_short_holdings instead — it doesn't " +
        "require guessing a kpiId/calcGroup/calc combination.")]
    public static async Task<string> GetKpiListScreener(
        BorsdataApiClient client,
        [Description("The Börsdata KPI id, e.g. 2 for P/E.")] int kpiId,
        [Description("The calculation group. 'last' is confirmed to work broadly; 'year'/'quarter'/'1year'/" +
            "'3year' are valid for some KPIs but not others (Börsdata returns HTTP 400 for an invalid " +
            "combination, e.g. 'year' does NOT work for kpiId 2/P/E) — if unsure, try 'last' first.")]
        string calcGroup,
        [Description("The calculation, e.g. 'latest', 'mean', 'high', 'low'.")] string calc,
        CancellationToken cancellationToken = default)
    {
        var values = await client.GetKpiListScreenerAsync(kpiId, calcGroup, calc, cancellationToken);
        var instruments = await client.GetInstrumentsAsync(cancellationToken);
        return BuildKpiListScreenerResult(values, instruments, kpiId, calcGroup, calc).ToJsonString();
    }

    [McpServerTool, Description(
        "Gets a calculated KPI value (e.g. P/E) for every instrument in Börsdata's global " +
        "(non-Nordic, Pro+) instrument universe in one call — the global counterpart to " +
        "get_kpi_list_screener, mirroring Börsdata's own separate kpislistglobalv1 endpoint (a " +
        "genuinely different endpoint, not a query switch on the Nordic one). Same no-sort/no-cap " +
        "shape as get_kpi_list_screener. Discover a global insId via list_instruments with " +
        "includeGlobal:true.")]
    public static async Task<string> GetGlobalKpiListScreener(
        BorsdataApiClient client,
        [Description("The Börsdata KPI id, e.g. 2 for P/E.")] int kpiId,
        [Description("The calculation group. 'last' is confirmed to work broadly; 'year'/'quarter'/'1year'/" +
            "'3year' are valid for some KPIs but not others (Börsdata returns HTTP 400 for an invalid " +
            "combination) — if unsure, try 'last' first.")]
        string calcGroup,
        [Description("The calculation, e.g. 'latest', 'mean', 'high', 'low'.")] string calc,
        CancellationToken cancellationToken = default)
    {
        var values = await client.GetGlobalKpiListScreenerAsync(kpiId, calcGroup, calc, cancellationToken);
        var instruments = await client.GetGlobalInstrumentsAsync(cancellationToken);
        return BuildKpiListScreenerResult(values, instruments, kpiId, calcGroup, calc).ToJsonString();
    }

    private static JsonObject BuildKpiListScreenerResult(
        JsonNode? valuesRoot, JsonNode? instrumentsRoot, int kpiId, string calcGroup, string calc)
    {
        var rawValues = (valuesRoot as JsonObject)?["values"] as JsonArray ?? [];
        var instrumentIndex = InstrumentLookup.BuildIndex(instrumentsRoot);

        // No sorting, filtering, or capping — Börsdata's own order, exactly as received, since
        // kpislistv1 has no sort/count parameter of its own to mirror.
        var values = new List<JsonNode>();
        foreach (var node in rawValues.OfType<JsonObject>())
        {
            if (node["i"] is not JsonValue idValue || !idValue.TryGetValue(out int insId))
                continue;

            var obj = new JsonObject { ["insId"] = insId };
            if (instrumentIndex.TryGetValue(insId, out var info))
            {
                obj["ticker"] = info.Ticker;
                obj["name"] = info.Name;
            }
            if (node["n"] is JsonValue nv && nv.TryGetValue(out double numeric))
                obj["value"] = numeric;
            else if (node["s"] is JsonValue sv && sv.TryGetValue(out string? str))
                obj["value"] = str;
            values.Add(obj);
        }

        return new JsonObject
        {
            ["kpiId"] = kpiId,
            ["calcGroup"] = calcGroup,
            ["calc"] = calc,
            ["values"] = new JsonArray(values.ToArray())
        };
    }
}
