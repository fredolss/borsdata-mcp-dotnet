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
}
