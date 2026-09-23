using System.Text.Json.Nodes;

namespace BorsdataMcp.Tools;

// Shared by tools that need to enrich a bare insId with ticker/name (GetKpiListScreener,
// GetShortHoldings) or parse a user-supplied comma-separated instrumentIds filter.
internal static class InstrumentLookup
{
    public static Dictionary<long, (string Name, string Ticker)> BuildIndex(JsonNode? instrumentsRoot)
    {
        // Börsdata wraps the array in an envelope object, e.g. { "instruments": [ ... ] }.
        var all = (instrumentsRoot as JsonObject)?["instruments"] as JsonArray ?? [];
        var index = new Dictionary<long, (string, string)>();

        foreach (var node in all.OfType<JsonObject>())
        {
            if (node["insId"] is JsonValue idValue && idValue.TryGetValue(out long insId) &&
                node["name"] is JsonValue nameValue && nameValue.TryGetValue(out string? name) &&
                node["ticker"] is JsonValue tickerValue && tickerValue.TryGetValue(out string? ticker))
            {
                index[insId] = (name, ticker);
            }
        }

        return index;
    }

    public static HashSet<long>? ParseIds(string? commaSeparatedIds)
    {
        if (string.IsNullOrWhiteSpace(commaSeparatedIds))
            return null;

        return commaSeparatedIds
            .Split(',')
            .Select(s => long.TryParse(s.Trim(), out var id) ? id : (long?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToHashSet();
    }
}
