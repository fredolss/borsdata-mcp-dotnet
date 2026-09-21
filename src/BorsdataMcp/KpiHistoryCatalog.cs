using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BorsdataMcp;

public sealed record KpiHistoryOption(
    int KpiId,
    string KpiName,
    string ReportType,
    string PriceType,
    string Description);

public sealed class KpiHistoryCatalog
{
    private const string ResourceName = "BorsdataMcp.Data.kpi-history-options.json";
    private readonly ImmutableArray<KpiHistoryOption> options;
    private readonly ImmutableHashSet<KpiHistoryKey> keys;

    public string Source { get; }
    public string Retrieved { get; }
    public IReadOnlyList<KpiHistoryOption> Options => options;

    public KpiHistoryCatalog()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded KPI history catalog '{ResourceName}' was not found.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        Source = root.GetProperty("source").GetString() ?? "";
        Retrieved = root.GetProperty("retrieved").GetString() ?? "";
        options = root.GetProperty("options")
            .EnumerateArray()
            .Select(item => new KpiHistoryOption(
                item.GetProperty("kpiId").GetInt32(),
                item.GetProperty("kpiName").GetString() ?? "",
                item.GetProperty("reportType").GetString() ?? "",
                item.GetProperty("priceType").GetString() ?? "",
                item.GetProperty("description").GetString() ?? ""))
            .ToImmutableArray();

        keys = options
            .Select(option => new KpiHistoryKey(option.KpiId, option.ReportType, option.PriceType))
            .ToImmutableHashSet();

        if (options.IsEmpty || keys.Count != options.Length)
            throw new InvalidOperationException("The embedded KPI history catalog is empty or contains duplicate combinations.");
    }

    public void Validate(int kpiId, string reportType, string priceType)
    {
        if (keys.Contains(new KpiHistoryKey(kpiId, reportType, priceType)))
            return;

        var sameKpi = options.Where(option => option.KpiId == kpiId).ToArray();
        if (sameKpi.Length == 0)
        {
            throw InvalidRequest($"KPI {kpiId} has no historical values in Börsdata's KPI History catalog. " +
                                 "Use list_kpi_history_options to choose a supported KPI.");
        }

        var combinations = string.Join(", ", sameKpi.Select(option => $"{option.ReportType}/{option.PriceType}"));
        throw InvalidRequest($"Invalid KPI history combination {kpiId}/{reportType}/{priceType}. " +
                             $"Valid combinations for KPI {kpiId}: {combinations}. " +
                             $"Call list_kpi_history_options with kpiId {kpiId}; do not guess.");
    }

    public string Search(int? kpiId, string? query, int? maxCount)
    {
        if (kpiId is null && string.IsNullOrWhiteSpace(query))
            throw InvalidRequest("Supply kpiId or query.");
        if (kpiId is <= 0)
            throw InvalidRequest("kpiId must be positive.");

        var take = maxCount ?? 100;
        if (take is < 1 or > 200)
            throw InvalidRequest("maxCount must be between 1 and 200.");

        var matches = options.AsEnumerable();
        if (kpiId is not null)
            matches = matches.Where(option => option.KpiId == kpiId.Value);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var (search, aliasKpiId) = CatalogSearch.ApplyKnownAlias(query.Trim());
            if (aliasKpiId is not null)
                matches = matches.Where(option => option.KpiId == aliasKpiId.Value);
            if (search.Length > 0)
            {
                matches = matches.Where(option =>
                    CatalogSearch.MatchesAllTerms(
                        search, option.KpiName, option.Description, option.ReportType, option.PriceType));
            }
        }

        var materialized = matches.ToArray();
        var returned = materialized.Take(take).Select(option => (JsonNode)new JsonObject
        {
            ["kpiId"] = option.KpiId,
            ["kpiName"] = option.KpiName,
            ["reportType"] = option.ReportType,
            ["priceType"] = option.PriceType,
            ["description"] = option.Description
        }).ToArray();

        return new JsonObject
        {
            ["source"] = Source,
            ["retrieved"] = Retrieved,
            ["totalMatched"] = materialized.Length,
            ["returned"] = returned.Length,
            ["options"] = new JsonArray(returned)
        }.ToJsonString();
    }

    private static InvalidOperationException InvalidRequest(string detail) =>
        new($"INVALID_REQUEST: {detail}");

    private readonly record struct KpiHistoryKey(int KpiId, string ReportType, string PriceType);
}
