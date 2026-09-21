using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BorsdataMcp;

public sealed record KpiFilterInput(int KpiId, string CalcGroup, string Calc, string Operator, double Value);

public sealed record KpiSortInput(int KpiId, string CalcGroup, string Calc, string Direction);

public sealed record InstrumentScreeningRequest(
    string? Cursor = null,
    bool? Global = null,
    int[]? CountryIds = null,
    int[]? MarketIds = null,
    int[]? SectorIds = null,
    int[]? BranchIds = null,
    KpiFilterInput[]? KpiFilters = null,
    KpiSortInput? SortBy = null,
    int? PageSize = null);

public sealed class InstrumentScreeningService(
    BorsdataApiClient client,
    IMemoryCache cache,
    IOptions<ScreeningOptions> options,
    TimeProvider timeProvider)
{
    private const string SnapshotKeyPrefix = "screening:snapshot:";
    private const string CursorKeyPrefix = "screening:cursor:";
    private static readonly HashSet<string> ValidOperators =
        new(["lt", "lte", "gt", "gte", "eq", "neq"], StringComparer.Ordinal);

    public async Task<string> ScreenAsync(InstrumentScreeningRequest request, CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        if (request.Cursor is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Cursor))
                throw CursorExpired();
            return GetNextPage(request);
        }

        return await CreateScreeningAsync(request, cancellationToken);
    }

    private async Task<string> CreateScreeningAsync(InstrumentScreeningRequest request, CancellationToken cancellationToken)
    {
        var filters = request.KpiFilters;
        if (filters is null || filters.Length == 0)
            throw InvalidRequest("At least one kpiFilter is required for a new screening.");

        ValidateIds(request.CountryIds, "countryIds");
        ValidateIds(request.MarketIds, "marketIds");
        ValidateIds(request.SectorIds, "sectorIds");
        ValidateIds(request.BranchIds, "branchIds");

        foreach (var filter in filters)
            ValidateFilter(filter);

        var uniqueKeys = filters
            .Select(ToKey)
            .Distinct()
            .ToImmutableArray();

        KpiKey? sortKey = null;
        var sortDescending = false;
        if (request.SortBy is not null)
        {
            ValidateKpiIdentity(request.SortBy.KpiId, request.SortBy.CalcGroup, request.SortBy.Calc, "sortBy");
            if (request.SortBy.Direction is not ("asc" or "desc"))
                throw InvalidRequest("sortBy.direction must be 'asc' or 'desc'.");

            sortKey = new KpiKey(request.SortBy.KpiId, request.SortBy.CalcGroup, request.SortBy.Calc);
            if (!uniqueKeys.Contains(sortKey.Value))
                throw InvalidRequest("sortBy must exactly match a KPI combination in kpiFilters.");
            sortDescending = request.SortBy.Direction == "desc";
        }

        var pageSize = request.PageSize ?? options.Value.DefaultPageSize;
        if (pageSize < 1 || pageSize > options.Value.MaxPageSize)
            throw InvalidRequest($"pageSize must be between 1 and {options.Value.MaxPageSize}.");

        var useGlobal = request.Global ?? false;
        JsonNode? instrumentsRoot;
        try
        {
            instrumentsRoot = useGlobal
                ? await client.GetGlobalInstrumentsAsync(cancellationToken)
                : await client.GetInstrumentsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"INSTRUMENT_FETCH_FAILED: Could not fetch {(useGlobal ? "global" : "Nordic")} instrument metadata. {ex.Message}", ex);
        }
        var candidates = ReadInstruments(instrumentsRoot)
            .Where(i => MatchesMetadata(i, request))
            .ToArray();

        var valuesByKpi = new Dictionary<KpiKey, Dictionary<int, double>>();
        foreach (var key in uniqueKeys)
        {
            try
            {
                var root = useGlobal
                    ? await client.GetGlobalKpiListScreenerAsync(key.KpiId, key.CalcGroup, key.Calc, cancellationToken)
                    : await client.GetKpiListScreenerAsync(key.KpiId, key.CalcGroup, key.Calc, cancellationToken);
                valuesByKpi[key] = ReadNumericValues(root);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"KPI_FETCH_FAILED: Could not fetch KPI {key.KpiId}/{key.CalcGroup}/{key.Calc}. {ex.Message}", ex);
            }
        }

        var matched = new List<ScreenedInstrument>();
        var unevaluableCount = 0;
        foreach (var instrument in candidates)
        {
            var values = new Dictionary<KpiKey, double>();
            var complete = true;
            foreach (var key in uniqueKeys)
            {
                if (!valuesByKpi[key].TryGetValue(instrument.InsId, out var value))
                {
                    complete = false;
                    break;
                }
                values[key] = value;
            }

            if (!complete)
            {
                unevaluableCount++;
                continue;
            }

            if (!filters.All(filter => Compare(values[ToKey(filter)], filter.Operator, filter.Value)))
                continue;

            matched.Add(new ScreenedInstrument(
                instrument.InsId,
                instrument.Ticker,
                instrument.Name,
                uniqueKeys.Select(key => new ScreenedKpi(key, values[key])).ToImmutableArray()));
        }

        matched.Sort((left, right) => CompareResults(left, right, sortKey, sortDescending));

        var now = timeProvider.GetUtcNow();
        var expiresAt = now.Add(options.Value.SnapshotTtl);
        var snapshot = new ScreeningSnapshot(
            Guid.NewGuid().ToString("N"),
            matched.ToImmutableArray(),
            matched.Count,
            unevaluableCount,
            now,
            expiresAt,
            pageSize);
        cache.Set(SnapshotKey(snapshot.ScreeningId), snapshot, expiresAt);

        return RenderPage(snapshot, 0);
    }

    private string GetNextPage(InstrumentScreeningRequest request)
    {
        if (HasScreeningParameters(request))
            throw InvalidRequest("When cursor is supplied, no other screening or paging parameters may be supplied.");

        if (!cache.TryGetValue(CursorKey(request.Cursor!), out CursorEntry? cursorEntry) || cursorEntry is null)
            throw CursorExpired();

        if (timeProvider.GetUtcNow() >= cursorEntry.ExpiresAt ||
            !cache.TryGetValue(SnapshotKey(cursorEntry.ScreeningId), out ScreeningSnapshot? snapshot) ||
            snapshot is null || timeProvider.GetUtcNow() >= snapshot.ExpiresAt)
        {
            throw CursorExpired();
        }

        return RenderPage(snapshot, cursorEntry.NextOffset);
    }

    private string RenderPage(ScreeningSnapshot snapshot, int offset)
    {
        var page = snapshot.Results.Skip(offset).Take(snapshot.PageSize).ToArray();
        var nextOffset = offset + page.Length;
        string? nextCursor = null;

        if (nextOffset < snapshot.TotalMatched)
        {
            nextCursor = CreateToken();
            cache.Set(
                CursorKey(nextCursor),
                new CursorEntry(snapshot.ScreeningId, nextOffset, snapshot.ExpiresAt),
                snapshot.ExpiresAt);
        }

        var results = page.Select(result => (JsonNode)new JsonObject
        {
            ["insId"] = result.InsId,
            ["ticker"] = result.Ticker,
            ["name"] = result.Name,
            ["kpis"] = new JsonArray(result.Kpis.Select(kpi => (JsonNode)new JsonObject
            {
                ["kpiId"] = kpi.Key.KpiId,
                ["calcGroup"] = kpi.Key.CalcGroup,
                ["calc"] = kpi.Key.Calc,
                ["value"] = kpi.Value
            }).ToArray())
        }).ToArray();

        return new JsonObject
        {
            ["totalMatched"] = snapshot.TotalMatched,
            ["returned"] = page.Length,
            ["pageSize"] = snapshot.PageSize,
            ["unevaluableCount"] = snapshot.UnevaluableCount,
            ["nextCursor"] = nextCursor,
            ["results"] = new JsonArray(results)
        }.ToJsonString();
    }

    private static IEnumerable<InstrumentData> ReadInstruments(JsonNode? root)
    {
        if ((root as JsonObject)?["instruments"] is not JsonArray instruments)
            throw new InvalidOperationException("INSTRUMENT_FETCH_FAILED: Börsdata returned no instruments array.");

        foreach (var node in instruments.OfType<JsonObject>())
        {
            if (!TryGetInt(node, "insId", out var insId))
                continue;

            yield return new InstrumentData(
                insId,
                GetString(node, "ticker"),
                GetString(node, "name"),
                GetNullableInt(node, "countryId"),
                GetNullableInt(node, "marketId"),
                GetNullableInt(node, "sectorId"),
                GetNullableInt(node, "branchId"));
        }
    }

    private static Dictionary<int, double> ReadNumericValues(JsonNode? root)
    {
        var result = new Dictionary<int, double>();
        if ((root as JsonObject)?["values"] is not JsonArray values)
            throw new InvalidDataException("Börsdata returned no values array.");
        foreach (var node in values.OfType<JsonObject>())
        {
            if (TryGetInt(node, "i", out var insId) &&
                node["n"] is JsonValue valueNode && valueNode.TryGetValue(out double value) &&
                double.IsFinite(value))
            {
                result[insId] = value;
            }
        }
        return result;
    }

    private static bool MatchesMetadata(InstrumentData instrument, InstrumentScreeningRequest request) =>
        MatchesId(instrument.CountryId, request.CountryIds) &&
        MatchesId(instrument.MarketId, request.MarketIds) &&
        MatchesId(instrument.SectorId, request.SectorIds) &&
        MatchesId(instrument.BranchId, request.BranchIds);

    private static bool MatchesId(int? value, int[]? allowed) =>
        allowed is null || allowed.Length == 0 || value is not null && allowed.Contains(value.Value);

    private static bool Compare(double actual, string op, double expected) => op switch
    {
        "lt" => actual < expected,
        "lte" => actual <= expected,
        "gt" => actual > expected,
        "gte" => actual >= expected,
        "eq" => actual == expected,
        "neq" => actual != expected,
        _ => false
    };

    private static int CompareResults(
        ScreenedInstrument left, ScreenedInstrument right, KpiKey? sortKey, bool descending)
    {
        if (sortKey is not null)
        {
            var leftValue = left.Kpis.First(k => k.Key == sortKey.Value).Value;
            var rightValue = right.Kpis.First(k => k.Key == sortKey.Value).Value;
            var valueComparison = descending ? rightValue.CompareTo(leftValue) : leftValue.CompareTo(rightValue);
            if (valueComparison != 0)
                return valueComparison;
        }
        return left.InsId.CompareTo(right.InsId);
    }

    private static bool HasScreeningParameters(InstrumentScreeningRequest request) =>
        request.Global is not null || request.CountryIds is not null || request.MarketIds is not null ||
        request.SectorIds is not null || request.BranchIds is not null || request.KpiFilters is not null ||
        request.SortBy is not null || request.PageSize is not null;

    private static void ValidateFilter(KpiFilterInput filter)
    {
        if (filter is null)
            throw InvalidRequest("kpiFilters must not contain null entries.");
        ValidateKpiIdentity(filter.KpiId, filter.CalcGroup, filter.Calc, "kpiFilter");
        if (!ValidOperators.Contains(filter.Operator))
            throw InvalidRequest($"Unsupported operator '{filter.Operator}'. Use lt, lte, gt, gte, eq, or neq.");
        if (!double.IsFinite(filter.Value))
            throw InvalidRequest("kpiFilter.value must be a finite number.");
    }

    private static void ValidateKpiIdentity(int kpiId, string calcGroup, string calc, string field)
    {
        if (kpiId <= 0)
            throw InvalidRequest($"{field}.kpiId must be positive.");
        if (string.IsNullOrWhiteSpace(calcGroup))
            throw InvalidRequest($"{field}.calcGroup is required.");
        if (string.IsNullOrWhiteSpace(calc))
            throw InvalidRequest($"{field}.calc is required.");
    }

    private static void ValidateIds(int[]? ids, string field)
    {
        if (ids?.Any(id => id <= 0) == true)
            throw InvalidRequest($"{field} may only contain positive IDs.");
    }

    private void ValidateConfiguration()
    {
        if (options.Value.DefaultPageSize < 1 ||
            options.Value.MaxPageSize < options.Value.DefaultPageSize ||
            options.Value.SnapshotTtl <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Invalid Screening configuration.");
        }
    }

    private static KpiKey ToKey(KpiFilterInput filter) =>
        new(filter.KpiId, filter.CalcGroup, filter.Calc);

    private static bool TryGetInt(JsonObject node, string property, out int value)
    {
        value = default;
        return node[property] is JsonValue jsonValue && jsonValue.TryGetValue(out value);
    }

    private static int? GetNullableInt(JsonObject node, string property) =>
        TryGetInt(node, property, out var value) ? value : null;

    private static string GetString(JsonObject node, string property) =>
        node[property] is JsonValue value && value.TryGetValue(out string? text) ? text ?? "" : "";

    private static string SnapshotKey(string screeningId) => SnapshotKeyPrefix + screeningId;
    private static string CursorKey(string cursor) => CursorKeyPrefix + cursor;

    private static string CreateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static InvalidOperationException InvalidRequest(string detail) =>
        new($"INVALID_REQUEST: {detail}");

    private static InvalidOperationException CursorExpired() =>
        new("CURSOR_EXPIRED: The cursor is invalid or expired. Start a new screening.");

    private readonly record struct KpiKey(int KpiId, string CalcGroup, string Calc);
    private sealed record InstrumentData(
        int InsId, string Ticker, string Name, int? CountryId, int? MarketId, int? SectorId, int? BranchId);
    private sealed record ScreenedKpi(KpiKey Key, double Value);
    private sealed record ScreenedInstrument(
        int InsId, string Ticker, string Name, ImmutableArray<ScreenedKpi> Kpis);
    private sealed record ScreeningSnapshot(
        string ScreeningId,
        ImmutableArray<ScreenedInstrument> Results,
        int TotalMatched,
        int UnevaluableCount,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        int PageSize);
    private sealed record CursorEntry(string ScreeningId, int NextOffset, DateTimeOffset ExpiresAt);
}
