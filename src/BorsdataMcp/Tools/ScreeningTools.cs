using System.ComponentModel;
using ModelContextProtocol.Server;

namespace BorsdataMcp.Tools;

[McpServerToolType]
public static class ScreeningTools
{
    [McpServerTool, Description(
        "Recommended tool for finding Nordic or global instruments that satisfy one or more " +
        "financial KPI conditions. It fetches complete Börsdata KPI lists internally, combines " +
        "all filters with AND logic, and returns only matching instruments. On the first call, " +
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
        [Description("Required on the first call. KPI conditions containing kpiId, calcGroup, calc, operator (lt/lte/gt/gte/eq/neq), and value. All conditions use AND logic.")]
        KpiFilterInput[]? kpiFilters = null,
        [Description("Optional KPI sort containing kpiId, calcGroup, calc, and direction (asc/desc). It must exactly match a KPI combination in kpiFilters. insId is always the tie-breaker.")]
        KpiSortInput? sortBy = null,
        [Description("Results per page. Default 50; maximum is configured by the server and initially 200.")]
        int? pageSize = null,
        CancellationToken cancellationToken = default) =>
        service.ScreenAsync(new InstrumentScreeningRequest(
            cursor, global, countryIds, marketIds, sectorIds, branchIds, kpiFilters, sortBy, pageSize), cancellationToken);
}
