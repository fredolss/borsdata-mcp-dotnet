using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace BorsdataMcp.Tools;

[McpServerToolType]
public static class CalendarTools
{
    [McpServerTool, Description(
        "Gets report release dates for specified instruments — Börsdata returns each instrument's " +
        "full history of past and already-scheduled future dates in one list, so pass fromDate " +
        "(e.g. today) to see only upcoming reports rather than the whole history. Returns one entry " +
        "per instrument: { insId, totalMatched, returned, reports }. Also works transparently for a " +
        "global (non-Nordic, Pro+) instrument's insId — discover one via ListInstruments with " +
        "includeGlobal:true.")]
    public static async Task<string> GetReportCalendar(
        BorsdataApiClient client,
        [Description("Comma-separated instrument insIds, from ListInstruments.")] string instrumentIds,
        [Description("Only include reports on or after this date, 'yyyy-MM-dd'. Optional — omit for full history.")]
        string? fromDate = null,
        [Description("Only include reports on or before this date, 'yyyy-MM-dd'. Optional.")]
        string? toDate = null,
        [Description("Maximum number of report dates to return per instrument, earliest-matching first. Omit to return all matches.")]
        int? maxCount = null,
        CancellationToken cancellationToken = default)
    {
        var raw = await client.GetReportCalendarAsync(instrumentIds, cancellationToken);
        return BuildCalendarResult(raw, "releaseDate", "reports", fromDate, toDate, maxCount).ToJsonString();
    }

    [McpServerTool, Description(
        "Gets dividend ex-dates and amounts for specified instruments — Börsdata returns each " +
        "instrument's full history of past and any already-scheduled future dividends in one list, " +
        "so pass fromDate (e.g. today) to see only upcoming ones rather than the whole history. " +
        "Returns one entry per instrument: { insId, totalMatched, returned, dividends }. Also works " +
        "transparently for a global (non-Nordic, Pro+) instrument's insId — discover one via " +
        "ListInstruments with includeGlobal:true.")]
    public static async Task<string> GetDividendCalendar(
        BorsdataApiClient client,
        [Description("Comma-separated instrument insIds, from ListInstruments.")] string instrumentIds,
        [Description("Only include dividends with an ex-date on or after this date, 'yyyy-MM-dd'. Optional — omit for full history.")]
        string? fromDate = null,
        [Description("Only include dividends with an ex-date on or before this date, 'yyyy-MM-dd'. Optional.")]
        string? toDate = null,
        [Description("Maximum number of dividend entries to return per instrument, earliest-matching first. Omit to return all matches.")]
        int? maxCount = null,
        CancellationToken cancellationToken = default)
    {
        var raw = await client.GetDividendCalendarAsync(instrumentIds, cancellationToken);
        return BuildCalendarResult(raw, "excludingDate", "dividends", fromDate, toDate, maxCount).ToJsonString();
    }

    // Shared by GetReportCalendar/GetDividendCalendar: both endpoints return
    // { "list": [ { "insId": .., "values": [ { <dateField>: .., ... }, ... ] }, ... ] } with the
    // instrument's entire past+future history and no server-side maxCount, so date-range filtering
    // and capping happen here rather than duplicating this per calendar tool.
    private static JsonObject BuildCalendarResult(
        JsonNode? root, string dateField, string resultsKey, string? fromDate, string? toDate, int? maxCount)
    {
        var list = (root as JsonObject)?["list"] as JsonArray ?? [];
        var from = fromDate is null ? (DateOnly?)null : DateOnly.Parse(fromDate);
        var to = toDate is null ? (DateOnly?)null : DateOnly.Parse(toDate);

        var instruments = new JsonArray();
        foreach (var node in list.OfType<JsonObject>())
        {
            if (node["insId"] is not JsonValue idValue || !idValue.TryGetValue(out int insId))
                continue;

            var entries = (node["values"] as JsonArray)?.OfType<JsonObject>() ?? [];
            var filtered = entries.Where(e => IsWithinDateRange(e, dateField, from, to)).ToList();
            var take = maxCount ?? filtered.Count;
            var page = new JsonArray(filtered.Take(take).Select(e => (JsonNode)e.DeepClone()).ToArray());

            instruments.Add((JsonNode)new JsonObject
            {
                ["insId"] = insId,
                ["totalMatched"] = filtered.Count,
                ["returned"] = page.Count,
                [resultsKey] = page
            });
        }

        return new JsonObject { ["instruments"] = instruments };
    }

    private static bool IsWithinDateRange(JsonObject entry, string dateField, DateOnly? from, DateOnly? to)
    {
        if (from is null && to is null)
            return true;
        if (entry[dateField] is not JsonValue dateValue || !dateValue.TryGetValue(out string? dateStr))
            return true;
        if (!DateOnly.TryParse(dateStr.Split('T')[0], out var date))
            return true;

        return (from is null || date >= from) && (to is null || date <= to);
    }
}
