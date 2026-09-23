using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace BorsdataMcp.Tools;

[McpServerToolType]
public static class MarketDataTools
{
    private const string LatestStockPricesCursorOwner = "get_latest_stock_prices";
    private const string StockPricesByDateCursorOwner = "get_stock_prices_by_date";

    [McpServerTool, Description(
        "Gets daily stock price history (open, high, low, close, volume) for one instrument. " +
        "Omit from/to/maxCount for the API's default window (10 years). maxCount is a lookback " +
        "window in YEARS (1-20, per Börsdata's own limit for this endpoint), not a count of " +
        "days/entries — pass from/to instead for an exact date range or a small recent window " +
        "(e.g. the last 30 days). Also works transparently for a global (non-Nordic, Pro+) " +
        "instrument's insId — discover one via search_instruments with universe:'global'.")]
    public static async Task<string> GetStockPrices(
        BorsdataApiClient client,
        [Description("The instrument's insId, from search_instruments.")] int instrumentId,
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

    [McpServerTool(UseStructuredContent = true), Description(
        "Gets the latest available daily OHLCV record for instruments; this is a daily close record, " +
        "not necessarily a real-time quote. Results include Börsdata's compact fields i/d/h/l/c/o/v " +
        "and are enriched with ticker/name. Restrict with instrumentIds when possible, choose the " +
        "Nordic or global data source, and sort by ticker or name. On the first call send filters, " +
        "sorting, and pageSize. If nextCursor is returned, request another page with only cursor, " +
        "and only when needed to answer the user's question.")]
    public static async Task<StockPricePageResult> GetLatestStockPrices(
        BorsdataApiClient client,
        CursorPaginationService pagination,
        [Description("Opaque continuation token returned as nextCursor by this tool. When supplied, omit every other parameter.")]
        string? cursor = null,
        [Description("Comma-separated instrument insIds to restrict results to, e.g. holdings resolved via search_instruments. Optional — omit to page through every instrument.")]
        string? instrumentIds = null,
        [Description("Query Börsdata's global (non-Nordic, Pro+) instrument universe instead of " +
            "the default Nordic one. This switches rather than merges the data source, unlike " +
            "search_instruments with universe:'all'. Default false.")]
        bool? global = null,
        [Description("Sort field: 'ticker' (default) or 'name'.")]
        string? sortBy = null,
        [Description("Sort direction: 'asc' (default) or 'desc'.")]
        string? sortDirection = null,
        [Description("Results per page. Default 50; maximum 200.")]
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        if (cursor is not null)
        {
            ValidateCursorOnly(instrumentIds, global, sortBy, sortDirection, pageSize);
            return ToStockPricePage(pagination.GetNextPage<StockPriceResult>(LatestStockPricesCursorOwner, cursor));
        }

        var useGlobal = global ?? false;
        var raw = useGlobal
            ? await client.GetGlobalLatestStockPricesAsync(cancellationToken)
            : await client.GetLatestStockPricesAsync(cancellationToken);
        var instruments = useGlobal
            ? await client.GetGlobalInstrumentsAsync(cancellationToken)
            : await client.GetInstrumentsAsync(cancellationToken);
        var results = BuildStockPriceResults(raw, instruments, instrumentIds, sortBy, sortDirection);
        return ToStockPricePage(pagination.CreatePage(LatestStockPricesCursorOwner, results, pageSize));
    }

    [McpServerTool(UseStructuredContent = true), Description(
        "Gets each instrument's stock price (open, high, low, close, volume) on a specific " +
        "historical date — the same data as get_latest_stock_prices but for a date you choose instead " +
        "of the latest available trading day. A weekend/holiday returns no results. Records use " +
        "Börsdata's compact i/d/h/l/c/o/v fields and include ticker/name. On the first call send date " +
        "plus optional instrumentIds, data source, sorting, and pageSize. If nextCursor is returned, " +
        "request another page with only cursor and only when needed.")]
    public static async Task<StockPricePageResult> GetStockPricesByDate(
        BorsdataApiClient client,
        CursorPaginationService pagination,
        [Description("The historical trading date, 'yyyy-MM-dd'. Required on the first call; omit when using cursor.")]
        string? date = null,
        [Description("Opaque continuation token returned as nextCursor by this tool. When supplied, omit every other parameter.")]
        string? cursor = null,
        [Description("Comma-separated instrument insIds to restrict results to, e.g. holdings resolved via search_instruments. Optional — omit to page through every instrument.")]
        string? instrumentIds = null,
        [Description("Query Börsdata's global (non-Nordic, Pro+) instrument universe instead of " +
            "the default Nordic one. This switches rather than merges the data source, unlike " +
            "search_instruments with universe:'all'. Default false.")]
        bool? global = null,
        [Description("Sort field: 'ticker' (default) or 'name'.")]
        string? sortBy = null,
        [Description("Sort direction: 'asc' (default) or 'desc'.")]
        string? sortDirection = null,
        [Description("Results per page. Default 50; maximum 200.")]
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        if (cursor is not null)
        {
            if (date is not null)
                throw InvalidRequest("When cursor is supplied, date and all other parameters must be omitted.");
            ValidateCursorOnly(instrumentIds, global, sortBy, sortDirection, pageSize);
            return ToStockPricePage(pagination.GetNextPage<StockPriceResult>(StockPricesByDateCursorOwner, cursor));
        }

        if (string.IsNullOrWhiteSpace(date) ||
            !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
        {
            throw InvalidRequest("date is required for a new request and must use 'yyyy-MM-dd'.");
        }

        var useGlobal = global ?? false;
        var raw = useGlobal
            ? await client.GetGlobalStockPricesByDateAsync(parsedDate, cancellationToken)
            : await client.GetStockPricesByDateAsync(parsedDate, cancellationToken);
        var instruments = useGlobal
            ? await client.GetGlobalInstrumentsAsync(cancellationToken)
            : await client.GetInstrumentsAsync(cancellationToken);
        var results = BuildStockPriceResults(raw, instruments, instrumentIds, sortBy, sortDirection);
        return ToStockPricePage(pagination.CreatePage(StockPricesByDateCursorOwner, results, pageSize));
    }

    private static List<StockPriceResult> BuildStockPriceResults(
        JsonNode? pricesRoot,
        JsonNode? instrumentsRoot,
        string? instrumentIds,
        string? sortBy,
        string? sortDirection)
    {
        var rawValues = (pricesRoot as JsonObject)?["stockPricesList"] as JsonArray ?? [];
        var instrumentIndex = InstrumentLookup.BuildIndex(instrumentsRoot);
        var idFilter = InstrumentLookup.ParseIds(instrumentIds);

        var matched = new List<StockPriceResult>();
        foreach (var node in rawValues.OfType<JsonObject>())
        {
            if (!TryGetLong(node, "i", out var insId) || !TryGetDouble(node, "c", out var close))
                continue;
            if (idFilter is not null && !idFilter.Contains(insId))
                continue;

            instrumentIndex.TryGetValue(insId, out var info);
            matched.Add(new StockPriceResult
            {
                InstrumentId = insId,
                Ticker = info.Ticker,
                Name = info.Name,
                Date = GetString(node, "d"),
                High = GetNullableDouble(node, "h"),
                Low = GetNullableDouble(node, "l"),
                Close = close,
                Open = GetNullableDouble(node, "o"),
                Volume = GetNullableLong(node, "v")
            });
        }

        return InstrumentSorting.Sort(
            matched,
            sortBy,
            sortDirection,
            value => value.Ticker,
            value => value.Name,
            value => value.InstrumentId);
    }

    private static void ValidateCursorOnly(
        string? instrumentIds,
        bool? global,
        string? sortBy,
        string? sortDirection,
        int? pageSize)
    {
        if (instrumentIds is not null || global is not null || sortBy is not null ||
            sortDirection is not null || pageSize is not null)
        {
            throw InvalidRequest("When cursor is supplied, no other filter, sorting, or paging parameters may be supplied.");
        }
    }

    private static StockPricePageResult ToStockPricePage(CursorPage<StockPriceResult> page) =>
        new()
        {
            TotalMatched = page.TotalMatched,
            Returned = page.Items.Count,
            Values = page.Items,
            NextCursor = page.NextCursor
        };

    private static bool TryGetLong(JsonObject node, string property, out long value)
    {
        value = default;
        return node[property] is JsonValue jsonValue && jsonValue.TryGetValue(out value);
    }

    private static long? GetNullableLong(JsonObject node, string property) =>
        TryGetLong(node, property, out var value) ? value : null;

    private static bool TryGetDouble(JsonObject node, string property, out double value)
    {
        value = default;
        return node[property] is JsonValue jsonValue && jsonValue.TryGetValue(out value);
    }

    private static double? GetNullableDouble(JsonObject node, string property) =>
        TryGetDouble(node, property, out var value) ? value : null;

    private static string? GetString(JsonObject node, string property) =>
        node[property] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static InvalidOperationException InvalidRequest(string detail) =>
        new($"INVALID_REQUEST: {detail}");

    [McpServerTool, Description("Gets a calculated KPI value (e.g. P/E, revenue growth) for one instrument. kpiId/calcGroup/calc must be an exact combination from list_kpi_screener_options; call that local lookup tool first when unsure and never guess. For every KPI at once for this instrument, use get_kpi_summary instead. For shorting/short-interest data specifically, use get_short_holdings instead. Also works transparently for a global (non-Nordic, Pro+) instrument's insId — discover one via search_instruments with universe:'global'.")]
    public static async Task<string> GetKpiScreener(
        BorsdataApiClient client,
        [Description("The instrument's insId, from search_instruments.")] int instrumentId,
        [Description("The Börsdata KPI id.")] int kpiId,
        [Description("The exact calculation group from list_kpi_screener_options.")]
        string calcGroup,
        [Description("The exact calculation from list_kpi_screener_options, e.g. 'latest', 'cagr', or 'mean'.")] string calc,
        CancellationToken cancellationToken) =>
        (await client.GetKpiScreenerAsync(instrumentId, kpiId, calcGroup, calc, cancellationToken))?.ToJsonString() ?? "{}";

    [McpServerTool, Description("Gets historical values for a KPI (e.g. P/E) over time for one instrument — how the metric has trended across periods, unlike get_kpi_screener which returns a single current value. kpiId/reportType/priceType must be an exact combination from list_kpi_history_options; call that local lookup tool first when unsure and never guess. Omit maxCount for the API's default window. Also works transparently for a global (non-Nordic, Pro+) instrument's insId — discover one via search_instruments with universe:'global'.")]
    public static async Task<string> GetKpiHistory(
        BorsdataApiClient client,
        KpiHistoryCatalog historyCatalog,
        [Description("The instrument's insId, from search_instruments.")] int instrumentId,
        [Description("The Börsdata KPI id, from list_kpi_metadata (e.g. 2 for P/E).")] int kpiId,
        [Description("The exact report type from list_kpi_history_options, e.g. 'year', 'r12', or 'quarter'.")] string reportType,
        [Description("The exact price type from list_kpi_history_options, normally 'mean', 'high', or 'low'. 'latest' is not a history price type.")] string priceType,
        [Description("Maximum number of most recent periods to return. Optional.")] int? maxCount = null,
        CancellationToken cancellationToken = default)
    {
        historyCatalog.Validate(kpiId, reportType, priceType);
        return (await client.GetKpiHistoryAsync(
            instrumentId, kpiId, reportType, priceType, maxCount, cancellationToken))?.ToJsonString() ?? "{}";
    }

    [McpServerTool, Description(
        "Gets historical values for a KPI (e.g. P/E) over time for a LIST of instruments in one " +
        "call — the bulk version of get_kpi_history. A direct mirror of Börsdata's own \"Kpi " +
        "History\" array endpoint: despite the similar-looking URL, this is a genuinely different " +
        "endpoint from get_kpi_list_screener (different Börsdata tag, different path params — " +
        "reportType/priceType here, not calcGroup/calc) and, unlike get_kpi_list_screener, this one " +
        "DOES filter server-side by instrumentIds — real API-level scoping, not something faked " +
        "client-side. kpiId/reportType/priceType must be an exact combination from " +
        "list_kpi_history_options; call that local lookup tool first when unsure and never guess. " +
        "Returns one entry per requested instrument, each with its own history array (or an error " +
        "field if that instrument's history couldn't be resolved). maxCount caps periods per " +
        "instrument (Börsdata's own limit: 20 for 'year', 40 for 'r12'/'quarter'), not the number " +
        "of instruments returned. Börsdata accepts at most 50 instrument IDs per call.")]
    public static async Task<string> GetKpiHistoryArray(
        BorsdataApiClient client,
        KpiHistoryCatalog historyCatalog,
        [Description("The Börsdata KPI id, from list_kpi_metadata (e.g. 2 for P/E).")] int kpiId,
        [Description("The exact report type from list_kpi_history_options, e.g. 'year', 'r12', or 'quarter'.")] string reportType,
        [Description("The exact price type from list_kpi_history_options, normally 'mean', 'high', or 'low'. 'latest' is not a history price type.")] string priceType,
        [Description("Comma-separated instrument insIds, from search_instruments. Required — Börsdata's own " +
            "API requires this for this endpoint and accepts at most 50 IDs per call.")]
        string instrumentIds,
        [Description("Maximum number of most recent periods to return per instrument. Optional.")] int? maxCount = null,
        CancellationToken cancellationToken = default)
    {
        historyCatalog.Validate(kpiId, reportType, priceType);
        return (await client.GetKpiHistoryArrayAsync(
            kpiId, reportType, priceType, instrumentIds, maxCount, cancellationToken))?.ToJsonString() ?? "{}";
    }

    [McpServerTool, Description("Gets every KPI Börsdata tracks (P/E, revenue growth, margins, etc.) for one instrument across multiple periods in one call — unlike get_kpi_screener, which returns a single value for one specific KPI. Each entry is keyed by KpiId (see list_kpi_metadata to resolve names); omit maxCount for the API's default number of periods per KPI. Also works transparently for a global (non-Nordic, Pro+) instrument's insId — discover one via search_instruments with universe:'global'.")]
    public static async Task<string> GetKpiSummary(
        BorsdataApiClient client,
        [Description("The instrument's insId, from search_instruments.")] int instrumentId,
        [Description("Report period: 'year', 'quarter', or 'r12'.")] string reportType,
        [Description("Maximum number of most recent periods to return per KPI. Optional.")] int? maxCount = null,
        CancellationToken cancellationToken = default) =>
        (await client.GetKpiSummaryAsync(instrumentId, reportType, maxCount, cancellationToken))?.ToJsonString() ?? "{}";

    [McpServerTool, Description("Gets financial reports (income statement, balance sheet, cash flow) for one instrument. Also works transparently for a global (non-Nordic, Pro+) instrument's insId — discover one via search_instruments with universe:'global'.")]
    public static async Task<string> GetReports(
        BorsdataApiClient client,
        [Description("The instrument's insId, from search_instruments.")] int instrumentId,
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
        [Description("The instrument's insId, from search_instruments.")] int instrumentId,
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
        [Description("Comma-separated instrument insIds, from search_instruments.")] string instrumentIds,
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
        "calc must be an exact combination from list_kpi_screener_options. Each result includes " +
        "ticker/name alongside insId. " +
        "This raw tool is intended for complete data retrieval, export, or custom processing. For " +
        "finding instruments that satisfy one or more KPI conditions, prefer screen_instruments. " +
        "For shorting/short-interest data specifically, use get_short_holdings instead — it doesn't " +
        "require guessing a kpiId/calcGroup/calc combination.")]
    public static async Task<string> GetKpiListScreener(
        BorsdataApiClient client,
        [Description("The Börsdata KPI id, e.g. 2 for P/E.")] int kpiId,
        [Description("The exact calculation group from list_kpi_screener_options.")]
        string calcGroup,
        [Description("The exact calculation from list_kpi_screener_options, e.g. 'latest', 'cagr', 'mean', 'high', or 'low'.")] string calc,
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
        "shape as get_kpi_list_screener. Discover a global insId via search_instruments with " +
        "universe:'global'. This raw tool is intended for complete data retrieval, export, or " +
        "custom processing; prefer screen_instruments for KPI-condition screening.")]
    public static async Task<string> GetGlobalKpiListScreener(
        BorsdataApiClient client,
        [Description("The Börsdata KPI id, e.g. 2 for P/E.")] int kpiId,
        [Description("The exact calculation group from list_kpi_screener_options.")]
        string calcGroup,
        [Description("The exact calculation from list_kpi_screener_options, e.g. 'latest', 'cagr', 'mean', 'high', or 'low'.")] string calc,
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
