using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace BorsdataMcp.Tools;

[McpServerToolType]
public static class ReferenceDataTools
{
    private const string SearchInstrumentsCursorOwner = "search_instruments";

    [McpServerTool, Description(
        "Searches Börsdata instruments by name, ticker, or ISIN and returns the insId used by other " +
        "tools. Optional market, country, sector, and branch filters can narrow the result. Choose " +
        "the Nordic, global (non-Nordic, Pro+), or combined universe. Results are sorted by ticker " +
        "or name and returned in cursor-paginated pages. On the first call, send any search/filter, " +
        "universe, sorting, and pageSize settings. If nextCursor is returned, fetch another page " +
        "with a new call containing only that cursor. Use filters when looking for specific " +
        "instruments and only fetch additional pages when needed to answer the user's question.")]
    public static async Task<string> SearchInstruments(
        BorsdataApiClient client,
        CursorPaginationService pagination,
        [Description("Opaque continuation token returned as nextCursor by a previous search_instruments call. When supplied, omit every other parameter.")]
        string? cursor = null,
        [Description("Case-insensitive substring match against the instrument's name, ticker, or ISIN. Optional.")]
        string? search = null,
        [Description("Filter to instruments on this market id, from list_markets. Optional. For KPI-based " +
            "screening within one or more markets, prefer screen_instruments with marketIds.")]
        int? marketId = null,
        [Description("Filter to instruments in this country id, from list_countries. Optional.")]
        int? countryId = null,
        [Description("Filter to instruments in this sector id, from list_sectors. Optional.")]
        int? sectorId = null,
        [Description("Filter to instruments in this industry branch id, from list_branches. Optional.")]
        int? branchId = null,
        [Description("Instrument population: 'nordic' (default), 'global' (non-Nordic, Pro+), or 'all'. Only the required Börsdata data source is fetched.")]
        string? universe = null,
        [Description("Sort field: 'ticker' (default) or 'name'. Sorting is case-insensitive and insId is the deterministic tie-breaker.")]
        string? sortBy = null,
        [Description("Sort direction: 'asc' (default) or 'desc'.")]
        string? sortDirection = null,
        [Description("Results per page. Default 50; maximum 200.")]
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        if (cursor is not null)
        {
            if (search is not null || marketId is not null || countryId is not null || sectorId is not null ||
                branchId is not null || universe is not null || sortBy is not null || sortDirection is not null ||
                pageSize is not null)
            {
                throw InvalidRequest("When cursor is supplied, no other search, sorting, or paging parameters may be supplied.");
            }

            return RenderInstrumentPage(pagination.GetNextPage<JsonObject>(SearchInstrumentsCursorOwner, cursor));
        }

        var selectedUniverse = universe ?? "nordic";
        if (selectedUniverse is not ("nordic" or "global" or "all"))
            throw InvalidRequest("universe must be 'nordic', 'global', or 'all'.");

        JsonNode? instruments = null;
        JsonNode? globalInstruments = null;
        if (selectedUniverse is "nordic" or "all")
            instruments = await client.GetInstrumentsAsync(cancellationToken);
        if (selectedUniverse is "global" or "all")
            globalInstruments = await client.GetGlobalInstrumentsAsync(cancellationToken);

        var matched = FilterAndSortInstruments(
            instruments, globalInstruments, selectedUniverse, search, marketId, countryId,
            sectorId, branchId, sortBy, sortDirection);
        return RenderInstrumentPage(pagination.CreatePage(SearchInstrumentsCursorOwner, matched, pageSize));
    }

    private static List<JsonObject> FilterAndSortInstruments(
        JsonNode? root, JsonNode? globalRoot, string universe, string? search, int? marketId,
        int? countryId, int? sectorId, int? branchId, string? sortBy, string? sortDirection)
    {
        // Börsdata wraps the array in an envelope object, e.g. { "instruments": [ ... ] },
        // rather than returning a bare JSON array.
        var all = (root as JsonObject)?["instruments"] as JsonArray ?? [];

        IEnumerable<JsonObject> CloneAll(JsonArray source) =>
            source.OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone());

        // insId is assumed unique across the Nordic and global universes (confirmed live: global
        // insIds start at 10054+, well above the Nordic range), so no de-duplication is needed.
        IEnumerable<JsonObject> query;
        if (universe == "all")
        {
            var allGlobal = (globalRoot as JsonObject)?["instruments"] as JsonArray ?? [];
            var nordic = CloneAll(all).Select(o => { o["isGlobal"] = false; return o; });
            var global = CloneAll(allGlobal).Select(o => { o["isGlobal"] = true; return o; });
            query = nordic.Concat(global);
        }
        else if (universe == "global")
        {
            var allGlobal = (globalRoot as JsonObject)?["instruments"] as JsonArray ?? [];
            query = CloneAll(allGlobal);
        }
        else
        {
            query = CloneAll(all);
        }

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(o => MatchesSearch(o, search));
        if (marketId is not null)
            query = query.Where(o => GetInt(o, "marketId") == marketId);
        if (countryId is not null)
            query = query.Where(o => GetInt(o, "countryId") == countryId);
        if (sectorId is not null)
            query = query.Where(o => GetInt(o, "sectorId") == sectorId);
        if (branchId is not null)
            query = query.Where(o => GetInt(o, "branchId") == branchId);

        return InstrumentSorting.Sort(
            query,
            sortBy,
            sortDirection,
            o => GetString(o, "ticker"),
            o => GetString(o, "name"),
            o => GetLong(o, "insId") ?? long.MaxValue);
    }

    private static string RenderInstrumentPage(CursorPage<JsonObject> page)
    {
        var result = new JsonObject
        {
            ["totalMatched"] = page.TotalMatched,
            ["returned"] = page.Items.Count,
            ["instruments"] = new JsonArray(page.Items.Select(o => (JsonNode)o.DeepClone()).ToArray())
        };
        if (page.NextCursor is not null)
            result["nextCursor"] = page.NextCursor;
        return result.ToJsonString();
    }

    private static bool MatchesSearch(JsonObject instrument, string search) =>
        FieldContains(instrument, "name", search) ||
        FieldContains(instrument, "ticker", search) ||
        FieldContains(instrument, "isin", search);

    private static bool FieldContains(JsonObject instrument, string field, string search) =>
        instrument[field] is JsonValue v && v.TryGetValue(out string? s) &&
        s.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static int? GetInt(JsonObject instrument, string field) =>
        instrument[field] is JsonValue v && v.TryGetValue(out int i) ? i : null;

    private static long? GetLong(JsonObject instrument, string field) =>
        instrument[field] is JsonValue v && v.TryGetValue(out long i) ? i : null;

    private static string? GetString(JsonObject instrument, string field) =>
        instrument[field] is JsonValue v && v.TryGetValue(out string? value) ? value : null;

    private static InvalidOperationException InvalidRequest(string detail) =>
        new($"INVALID_REQUEST: {detail}");

    [McpServerTool, Description("Lists all markets known to Börsdata (e.g. Stockholm Large Cap, First North).")]
    public static async Task<string> ListMarkets(BorsdataApiClient client, CancellationToken cancellationToken) =>
        (await client.GetMarketsAsync(cancellationToken))?.ToJsonString() ?? "{}";

    [McpServerTool, Description("Lists all industry branches known to Börsdata.")]
    public static async Task<string> ListBranches(BorsdataApiClient client, CancellationToken cancellationToken) =>
        (await client.GetBranchesAsync(cancellationToken))?.ToJsonString() ?? "{}";

    [McpServerTool, Description("Lists all sectors known to Börsdata.")]
    public static async Task<string> ListSectors(BorsdataApiClient client, CancellationToken cancellationToken) =>
        (await client.GetSectorsAsync(cancellationToken))?.ToJsonString() ?? "{}";

    [McpServerTool, Description("Lists all countries known to Börsdata.")]
    public static async Task<string> ListCountries(BorsdataApiClient client, CancellationToken cancellationToken) =>
        (await client.GetCountriesAsync(cancellationToken))?.ToJsonString() ?? "{}";

    [McpServerTool, Description("Lists all KPIs known to Börsdata (kpiId, Swedish/English name, display format, whether the value is a string). Use this to find the kpiId for screen_instruments/get_kpi_screener/get_kpi_history/get_kpi_list_screener — e.g. P/E, dividend yield.")]
    public static async Task<string> ListKpiMetadata(BorsdataApiClient client, CancellationToken cancellationToken) =>
        (await client.GetKpiMetadataAsync(cancellationToken))?.ToJsonString() ?? "{}";

    [McpServerTool, Description("Lists metadata for every field returned by get_reports/get_kpi_summary (property name, Swedish/English display name, format, e.g. 'cash_And_Equivalents' -> 'Cash and equivalents'). Use this to look up what a report field name means.")]
    public static async Task<string> ListReportMetadata(BorsdataApiClient client, CancellationToken cancellationToken) =>
        (await client.GetReportMetadataAsync(cancellationToken))?.ToJsonString() ?? "{}";

    [McpServerTool, Description("Lists Börsdata's translation table (translationKey plus Swedish/English name) used for various coded labels across the API, e.g. sector/branch names.")]
    public static async Task<string> ListTranslationMetadata(BorsdataApiClient client, CancellationToken cancellationToken) =>
        (await client.GetTranslationMetadataAsync(cancellationToken))?.ToJsonString() ?? "{}";

    [McpServerTool, Description(
        "Gets Swedish/English company description text for a list of instruments in one call — a " +
        "direct mirror of Börsdata's own \"Instrument Description\" endpoint, capped at 50 " +
        "instruments per call (Börsdata's own limit). Returns one entry per instrument: " +
        "{ insId, languageCode, text } (or an error field if that instrument's description " +
        "couldn't be resolved).")]
    public static async Task<string> GetInstrumentDescriptions(
        BorsdataApiClient client,
        [Description("Comma-separated instrument insIds, from search_instruments. Max 50.")] string instrumentIds,
        CancellationToken cancellationToken) =>
        (await client.GetInstrumentDescriptionsAsync(instrumentIds, cancellationToken))?.ToJsonString() ?? "{}";

    [McpServerTool, Description(
        "Lists all stock splits and reverse splits across Börsdata's instruments — split date, " +
        "ratio (e.g. '1:100'), and type. Each result is enriched with ticker/name. Returns " +
        "{ splits }.")]
    public static async Task<string> GetStockSplits(BorsdataApiClient client, CancellationToken cancellationToken)
    {
        var raw = await client.GetStockSplitsAsync(cancellationToken);
        var instruments = await client.GetInstrumentsAsync(cancellationToken);
        return BuildStockSplitsResult(raw, instruments).ToJsonString();
    }

    private static JsonObject BuildStockSplitsResult(JsonNode? root, JsonNode? instrumentsRoot)
    {
        // Unlike every other Börsdata endpoint, this one uses "instrumentId" rather than "insId" —
        // confirmed live.
        var list = (root as JsonObject)?["stockSplitList"] as JsonArray ?? [];
        var instrumentIndex = InstrumentLookup.BuildIndex(instrumentsRoot);

        var splits = new JsonArray();
        foreach (var node in list.OfType<JsonObject>())
        {
            var obj = (JsonObject)node.DeepClone();
            if (node["instrumentId"] is JsonValue idValue && idValue.TryGetValue(out int insId) &&
                instrumentIndex.TryGetValue(insId, out var info))
            {
                obj["ticker"] = info.Ticker;
                obj["name"] = info.Name;
            }
            splits.Add((JsonNode)obj);
        }

        return new JsonObject { ["splits"] = splits };
    }

    [McpServerTool, Description(
        "Gets the last-updated timestamp for instruments Börsdata has recently updated, sorted " +
        "most-recently-updated first — useful for checking whether an instrument's data is fresh " +
        "before relying on it. This does NOT cover every instrument (confirmed: only ~700 of " +
        "roughly 1,700+ appear at any given time) — an instrument missing from the results doesn't " +
        "mean it doesn't exist, just that it hasn't updated recently. Each result is enriched with " +
        "ticker/name. Returns { totalMatched, returned, values }.")]
    public static async Task<string> GetInstrumentsUpdated(
        BorsdataApiClient client,
        [Description("Comma-separated instrument insIds to restrict results to. Optional — omit to get every recently-updated instrument. An id not present in the results may simply not have updated recently.")]
        string? instrumentIds = null,
        [Description("Maximum number of results to return. Omit to return all matches.")]
        int? maxCount = null,
        CancellationToken cancellationToken = default)
    {
        var raw = await client.GetInstrumentsUpdatedAsync(cancellationToken);
        var instruments = await client.GetInstrumentsAsync(cancellationToken);
        return BuildInstrumentsUpdatedResult(raw, instruments, instrumentIds, maxCount).ToJsonString();
    }

    private static JsonObject BuildInstrumentsUpdatedResult(
        JsonNode? root, JsonNode? instrumentsRoot, string? instrumentIds, int? maxCount)
    {
        var list = (root as JsonObject)?["instruments"] as JsonArray ?? [];
        var instrumentIndex = InstrumentLookup.BuildIndex(instrumentsRoot);
        var idFilter = InstrumentLookup.ParseIds(instrumentIds);

        var matched = new List<(int InsId, string UpdatedAt, JsonObject Raw)>();
        foreach (var node in list.OfType<JsonObject>())
        {
            if (node["insId"] is not JsonValue idValue || !idValue.TryGetValue(out int insId))
                continue;
            if (idFilter is not null && !idFilter.Contains(insId))
                continue;

            var updatedAt = node["updatedAt"] is JsonValue uv && uv.TryGetValue(out string? u) ? u : "";
            matched.Add((insId, updatedAt, node));
        }

        // Most-recently-updated first, matching the recency-first convention used elsewhere for
        // "activity" data (GetInsiderHoldings/GetBuybackHoldings).
        matched.Sort((a, b) => string.CompareOrdinal(b.UpdatedAt, a.UpdatedAt));

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

    [McpServerTool, Description("Gets the single global timestamp for when Börsdata's KPI calculations were last refreshed across all instruments — not per-instrument, unlike get_instruments_updated. Returns { kpisCalcUpdated }.")]
    public static async Task<string> GetKpisUpdated(BorsdataApiClient client, CancellationToken cancellationToken) =>
        (await client.GetKpisUpdatedAsync(cancellationToken))?.ToJsonString() ?? "{}";
}
