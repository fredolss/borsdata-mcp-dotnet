using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BorsdataMcp;

public sealed record KpiScreenerOption(
    int KpiId,
    string KpiName,
    string CalcGroup,
    string Calc,
    string Description);

public sealed class KpiScreenerCatalog
{
    private const string ResourceName = "BorsdataMcp.Data.kpi-screener-options.json";
    private readonly ImmutableArray<KpiScreenerOption> options;
    private readonly ImmutableHashSet<KpiScreenerKey> keys;

    public string Source { get; }
    public string Retrieved { get; }
    public IReadOnlyList<KpiScreenerOption> Options => options;

    public KpiScreenerCatalog()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded KPI screener catalog '{ResourceName}' was not found.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        Source = root.GetProperty("source").GetString() ?? "";
        Retrieved = root.GetProperty("retrieved").GetString() ?? "";
        options = root.GetProperty("options")
            .EnumerateArray()
            .Select(item => new KpiScreenerOption(
                item.GetProperty("kpiId").GetInt32(),
                item.GetProperty("kpiName").GetString() ?? "",
                item.GetProperty("calcGroup").GetString() ?? "",
                item.GetProperty("calc").GetString() ?? "",
                item.GetProperty("description").GetString() ?? ""))
            .ToImmutableArray();

        keys = options
            .Select(option => new KpiScreenerKey(option.KpiId, option.CalcGroup, option.Calc))
            .ToImmutableHashSet();

        if (options.IsEmpty || keys.Count != options.Length)
            throw new InvalidOperationException("The embedded KPI screener catalog is empty or contains duplicate combinations.");
    }

    public bool Contains(int kpiId, string calcGroup, string calc) =>
        keys.Contains(new KpiScreenerKey(kpiId, calcGroup, calc));

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
            var search = query.Trim();
            matches = matches.Where(option =>
                option.KpiName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                option.Description.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                option.CalcGroup.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                option.Calc.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        var materialized = matches.ToArray();
        var returned = materialized.Take(take).Select(option => (JsonNode)new JsonObject
        {
            ["kpiId"] = option.KpiId,
            ["kpiName"] = option.KpiName,
            ["calcGroup"] = option.CalcGroup,
            ["calc"] = option.Calc,
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

    public string DescribeInvalidCombination(int kpiId, string calcGroup, string calc)
    {
        var sameKpi = options.Where(option => option.KpiId == kpiId).ToArray();
        if (sameKpi.Length == 0)
            return $"KPI {kpiId} is not present in the bundled screener catalog. " +
                   "Use list_kpi_screener_options or list_kpi_metadata to choose a supported KPI.";

        var groups = string.Join(", ", sameKpi.Select(option => option.CalcGroup).Distinct());
        var calculations = string.Join(", ", sameKpi.Select(option => option.Calc).Distinct());
        return $"Invalid KPI combination {kpiId}/{calcGroup}/{calc}. " +
               $"Known calcGroup values for KPI {kpiId}: {groups}. Known calc values: {calculations}. " +
               $"Call list_kpi_screener_options with kpiId {kpiId} for exact valid combinations; do not guess.";
    }

    private static InvalidOperationException InvalidRequest(string detail) =>
        new($"INVALID_REQUEST: {detail}");

    private readonly record struct KpiScreenerKey(int KpiId, string CalcGroup, string Calc);
}
