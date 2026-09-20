using System.Text.Json.Nodes;

namespace BorsdataMcp.Tools;

// Shared by tools that need to enrich a bare insId with ticker/name (GetKpiListScreener,
// GetShortHoldings) or parse a user-supplied comma-separated instrumentIds filter.
internal static class InstrumentLookup
{
    public static Dictionary<int, (string Name, string Ticker)> BuildIndex(JsonNode? instrumentsRoot)
    {
        // Börsdata wraps the array in an envelope object, e.g. { "instruments": [ ... ] }.
        var all = (instrumentsRoot as JsonObject)?["instruments"] as JsonArray ?? [];
        var index = new Dictionary<int, (string, string)>();

        foreach (var node in all.OfType<JsonObject>())
        {
            if (node["insId"] is JsonValue idValue && idValue.TryGetValue(out int insId) &&
                node["name"] is JsonValue nameValue && nameValue.TryGetValue(out string? name) &&
                node["ticker"] is JsonValue tickerValue && tickerValue.TryGetValue(out string? ticker))
            {
                index[insId] = (name, ticker);
            }
        }

        return index;
    }

    public static HashSet<int>? ParseIds(string? commaSeparatedIds)
    {
        if (string.IsNullOrWhiteSpace(commaSeparatedIds))
            return null;

        return commaSeparatedIds
            .Split(',')
            .Select(s => int.TryParse(s.Trim(), out var id) ? id : (int?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToHashSet();
    }

    // Resolves marketId/countryId/sectorId/branchId (the same attributes ListInstruments filters
    // on) directly against the cached instrument list, so a caller can scope a market-wide tool
    // (e.g. GetKpiListScreener) to e.g. "Large Cap" in one call instead of first calling
    // ListInstruments to resolve insIds and chaining them into a second call as instrumentIds —
    // a two-call chain that, confirmed live, callers unreliably skip or forget on the first try.
    public static HashSet<int>? FilterIdsByAttributes(
        JsonNode? instrumentsRoot, int? marketId, int? countryId, int? sectorId, int? branchId)
    {
        if (marketId is null && countryId is null && sectorId is null && branchId is null)
            return null;

        var all = (instrumentsRoot as JsonObject)?["instruments"] as JsonArray ?? [];
        var matched = new HashSet<int>();
        foreach (var node in all.OfType<JsonObject>())
        {
            if (node["insId"] is not JsonValue idValue || !idValue.TryGetValue(out int insId))
                continue;
            if (marketId is not null && GetInt(node, "marketId") != marketId)
                continue;
            if (countryId is not null && GetInt(node, "countryId") != countryId)
                continue;
            if (sectorId is not null && GetInt(node, "sectorId") != sectorId)
                continue;
            if (branchId is not null && GetInt(node, "branchId") != branchId)
                continue;
            matched.Add(insId);
        }

        return matched;
    }

    private static int? GetInt(JsonObject instrument, string field) =>
        instrument[field] is JsonValue v && v.TryGetValue(out int i) ? i : null;
}
