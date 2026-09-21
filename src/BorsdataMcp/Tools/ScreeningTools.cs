using System.ComponentModel;
using ModelContextProtocol.Server;

namespace BorsdataMcp.Tools;

[McpServerToolType]
public static class ScreeningTools
{
    [McpServerTool, Description(
        "Looks up the exact kpiId/reportType/priceType combinations accepted by get_kpi_history " +
        "and get_kpi_history_array. The data comes from a complete local copy of Börsdata's " +
        "official KPI History table, so this call makes no Börsdata API request. Use kpiId after " +
        "resolving a metric with list_kpi_metadata, or search by KPI name. Call this before either " +
        "history tool whenever the exact combination is unknown; never invent priceType values. " +
        "Example: earnings/share (KPI 6) supports year/mean, not year/latest.")]
    public static string ListKpiHistoryOptions(
        KpiHistoryCatalog catalog,
        [Description("Optional exact KPI id, normally obtained from list_kpi_metadata.")]
        int? kpiId = null,
        [Description("Optional case-insensitive, word-order-independent search across KPI name, description, reportType, and priceType.")]
        string? query = null,
        [Description("Maximum combinations to return. Default 100; maximum 200.")]
        int? maxCount = null) =>
        catalog.Search(kpiId, query, maxCount);

    [McpServerTool, Description(
        "Looks up the exact calcGroup/calc combinations accepted by Börsdata's KPI screener " +
        "endpoints. The data comes from a complete local copy of Börsdata's official KPI Screener " +
        "List, so this call makes no Börsdata API request. Use kpiId after resolving a metric with " +
        "list_kpi_metadata, or search by its English name. Call this before screen_instruments, " +
        "get_kpi_screener, or a raw KPI list tool whenever the exact combination is unknown; never " +
        "invent calcGroup/calc values. Examples: P/E is 2/last/latest; five-year earnings-growth " +
        "CAGR is 97/5year/cagr.")]
    public static string ListKpiScreenerOptions(
        KpiScreenerCatalog catalog,
        [Description("Optional exact KPI id, normally obtained from list_kpi_metadata.")]
        int? kpiId = null,
        [Description("Optional case-insensitive, word-order-independent search across English KPI name, description, calcGroup, and calc.")]
        string? query = null,
        [Description("Maximum combinations to return. Default 100; maximum 200.")]
        int? maxCount = null) =>
        catalog.Search(kpiId, query, maxCount);

    [McpServerTool, Description(
        "Recommended tool for finding Nordic or global instruments that satisfy one or more " +
        "financial KPI conditions. It fetches complete Börsdata KPI lists internally, combines " +
        "all filters with AND logic, and returns only matching instruments. kpiId/calcGroup/calc " +
        "must be an exact combination from list_kpi_screener_options; call that tool first when " +
        "unsure and never guess. Common examples: P/E is 2/last/latest and five-year earnings-" +
        "growth CAGR is 97/5year/cagr. On the first call, " +
        "send kpiFilters plus any universe, metadata, sorting, and pageSize settings. If nextCursor " +
        "is returned, fetch the next page with a new call containing only that cursor. Never " +
        "construct or modify a cursor. Only fetch more pages when needed to answer the user's question.")]
    public static Task<string> ScreenInstruments(
        InstrumentScreeningService service,
        [Description("Opaque continuation token returned as nextCursor by a previous screen_instruments call. When supplied, omit every other parameter.")]
        string? cursor = null,
        [Description("False or omitted selects Nordic instruments; true selects Börsdata's global (non-Nordic, Pro+) universe.")]
        bool? global = null,
        [Description("Optional country IDs from list_countries. Multiple IDs are ORed.")]
        int[]? countryIds = null,
        [Description("Optional market IDs from list_markets. Multiple IDs are ORed.")]
        int[]? marketIds = null,
        [Description("Optional sector IDs from list_sectors. Multiple IDs are ORed.")]
        int[]? sectorIds = null,
        [Description("Optional industry branch IDs from list_branches. Multiple IDs are ORed.")]
        int[]? branchIds = null,
        [Description("Required on the first call. KPI conditions containing an exact kpiId/calcGroup/calc combination from list_kpi_screener_options, operator (lt/lte/gt/gte/eq/neq), and value. All conditions use AND logic.")]
        KpiFilterInput[]? kpiFilters = null,
        [Description("Optional KPI sort containing kpiId, calcGroup, calc, and direction (asc/desc). It must exactly match a KPI combination in kpiFilters. insId is always the tie-breaker.")]
        KpiSortInput? sortBy = null,
        [Description("Results per page. Default 50; maximum is configured by the server and initially 200.")]
        int? pageSize = null,
        CancellationToken cancellationToken = default) =>
        service.ScreenAsync(new InstrumentScreeningRequest(
            cursor, global, countryIds, marketIds, sectorIds, branchIds, kpiFilters, sortBy, pageSize), cancellationToken);
}
