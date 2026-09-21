using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace BorsdataMcp.Tools;

[McpServerToolType]
public static class ReferenceDataTools
{
    [McpServerTool, Description(
        "Lists instruments (stocks/funds) on Börsdata with IDs, names, tickers, ISINs, and " +
        "references to market/sector/branch/country. The returned insId is used by other tools. " +
        "Prefer search and/or the id filters to narrow results — calling this with no filters " +
        "returns every instrument on Börsdata (several thousand) in one response. Use maxCount " +
        "to cap the number returned; the response's totalMatched field tells you whether more " +
        "instruments matched than were returned. By default this only searches Börsdata's Nordic " +
        "instrument list — a search for a non-Nordic company/ticker (e.g. a US, Canadian, or other " +
        "international listing) will come back with totalMatched: 0 even though Börsdata covers it. " +
        "If a search returns no match, retry the same search with includeGlobal: true before " +
        "concluding the instrument isn't on Börsdata.")]
    public static async Task<string> ListInstruments(
        BorsdataApiClient client,
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
        [Description("Also include Börsdata's global (non-Nordic, Pro+) instrument universe " +
            "alongside the default Nordic list, tagging each result isGlobal. Default false " +
            "preserves the original Nordic-only output exactly (no isGlobal field appears at all " +
            "unless this is true). The global list is large (~16,000 instruments) but cached the " +
            "same way as the Nordic list, so repeated calls only pay the extra fetch once per week.")]
        bool includeGlobal = false,
        [Description("Maximum number of matching instruments to return. Omit to return all matches.")]
        int? maxCount = null,
        CancellationToken cancellationToken = default)
    {
        var instruments = await client.GetInstrumentsAsync(cancellationToken);
        var globalInstruments = includeGlobal ? await client.GetGlobalInstrumentsAsync(cancellationToken) : null;
        return FilterInstruments(instruments, globalInstruments, search, marketId, countryId, sectorId, branchId, maxCount).ToJsonString();
    }

    private static JsonObject FilterInstruments(
        JsonNode? root, JsonNode? globalRoot, string? search, int? marketId, int? countryId,
        int? sectorId, int? branchId, int? maxCount)
    {
        // Börsdata wraps the array in an envelope object, e.g. { "instruments": [ ... ] },
        // rather than returning a bare JSON array.
        var all = (root as JsonObject)?["instruments"] as JsonArray ?? [];

        IEnumerable<JsonObject> CloneAll(JsonArray source) =>
            source.OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone());

        // insId is assumed unique across the Nordic and global universes (confirmed live: global
        // insIds start at 10054+, well above the Nordic range), so no de-duplication is needed.
        IEnumerable<JsonObject> query;
        if (globalRoot is not null)
        {
            // Only tag isGlobal when global data is actually in play, so includeGlobal=false
            // produces byte-identical output to before this feature existed.
            var allGlobal = (globalRoot as JsonObject)?["instruments"] as JsonArray ?? [];
            var nordic = CloneAll(all).Select(o => { o["isGlobal"] = false; return o; });
            var global = CloneAll(allGlobal).Select(o => { o["isGlobal"] = true; return o; });
            query = nordic.Concat(global);
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

        var matched = query.ToList();
        var take = maxCount ?? matched.Count;

        // Entries are already freestanding clones from CloneAll() above (a JsonNode can only have
        // one parent, so this cloning still has to happen somewhere before insertion into `page`
        // below — it just happens earlier now, up front, rather than at paging time).
        var page = new JsonArray(matched.Take(take).Select(o => (JsonNode)o).ToArray());

        return new JsonObject
        {
            ["totalMatched"] = matched.Count,
            ["returned"] = page.Count,
            ["instruments"] = page
        };
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
        [Description("Comma-separated instrument insIds, from list_instruments. Max 50.")] string instrumentIds,
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
