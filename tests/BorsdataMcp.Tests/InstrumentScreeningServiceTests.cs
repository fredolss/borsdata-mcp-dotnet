using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BorsdataMcp.Tools;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BorsdataMcp.Tests;

public class InstrumentScreeningServiceTests
{
    private const string InstrumentsFixture = """
    { "instruments": [
      { "insId": 1, "name": "Alpha", "ticker": "AAA", "marketId": 1, "countryId": 1, "sectorId": 1, "branchId": 1 },
      { "insId": 2, "name": "Beta", "ticker": "BBB", "marketId": 1, "countryId": 2, "sectorId": 1, "branchId": 2 },
      { "insId": 3, "name": "Gamma", "ticker": "CCC", "marketId": 2, "countryId": 3, "sectorId": 2, "branchId": 3 },
      { "insId": 4, "name": "Delta", "ticker": "DDD", "marketId": 2, "countryId": 1, "sectorId": 2, "branchId": 4 },
      { "insId": 5, "name": "Epsilon", "ticker": "EEE", "marketId": 3, "countryId": 2, "sectorId": 3, "branchId": 5 },
      { "insId": 6, "name": "Zeta", "ticker": "ZZZ", "marketId": 1, "countryId": 1, "sectorId": 1, "branchId": 1 }
    ] }
    """;

    private const string GlobalInstrumentsFixture = """
    { "instruments": [
      { "insId": 100, "name": "Global Alpha", "ticker": "GAA", "marketId": 10, "countryId": 10, "sectorId": 10, "branchId": 10 },
      { "insId": 101, "name": "Global Beta", "ticker": "GBB", "marketId": 11, "countryId": 11, "sectorId": 11, "branchId": 11 }
    ] }
    """;

    private sealed class ScreeningStubHandler : DelegatingHandler
    {
        private readonly List<Uri> requestUris = [];
        public IReadOnlyList<Uri> RequestUris => requestUris;
        public int CallCount => requestUris.Count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requestUris.Add(request.RequestUri!);
            var path = request.RequestUri!.AbsolutePath;

            if (path.Contains("/kpis/2/15year/high"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("Invalid KPI combination", Encoding.UTF8, "text/plain")
                });
            }

            var body = path switch
            {
                var p when p.Contains("/global/kpis/1/") => KpiJson(1, (100, 4), (101, 8)),
                var p when p.EndsWith("/instruments/global") => GlobalInstrumentsFixture,
                var p when p.Contains("/kpis/1/") => KpiJson(1, (1, 5), (2, 10), (3, 15), (4, null), (5, "N/A"), (6, 10)),
                var p when p.Contains("/kpis/2/") => KpiJson(2, (1, 100), (2, 90), (3, 80), (4, 70), (5, 60)),
                var p when p.Contains("/kpis/3/") => KpiJson(3, (1, 1), (2, 2), (3, 3), (4, 4), (5, 5), (6, 6)),
                _ => InstrumentsFixture
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }

        private static string KpiJson(int kpiId, params (int Id, object? Value)[] values)
        {
            var entries = values.Select(item => item.Value switch
            {
                double d => $"{{\"i\":{item.Id},\"n\":{d.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"s\":null}}",
                int i => $"{{\"i\":{item.Id},\"n\":{i},\"s\":null}}",
                string s => $"{{\"i\":{item.Id},\"n\":null,\"s\":\"{s}\"}}",
                _ => $"{{\"i\":{item.Id},\"n\":null,\"s\":null}}"
            });
            return $"{{\"kpiId\":{kpiId},\"values\":[{string.Join(',', entries)}]}}";
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan duration) => Now = Now.Add(duration);
    }

    private static InstrumentScreeningService CreateService(
        out ScreeningStubHandler stub,
        out MutableTimeProvider timeProvider,
        IMemoryCache? cache = null,
        ScreeningOptions? screeningOptions = null)
    {
        stub = new ScreeningStubHandler();
        // Deliberately far from the real system clock: cache lifetime must follow this injected
        // clock semantically without passing its absolute timestamps to IMemoryCache.
        timeProvider = new MutableTimeProvider(new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var sharedCache = cache ?? new MemoryCache(new MemoryCacheOptions());
        var httpClient = new HttpClient(stub) { BaseAddress = new Uri("https://apiservice.borsdata.se/v1/") };
        var client = new BorsdataApiClient(httpClient, sharedCache);
        return new InstrumentScreeningService(
            client,
            sharedCache,
            Options.Create(screeningOptions ?? new ScreeningOptions()),
            timeProvider,
            new KpiScreenerCatalog());
    }

    private static KpiFilterInput Filter(
        int kpiId = 1, string op = "gte", double value = 0, string calcGroup = "last", string calc = "latest") =>
        new(kpiId, calcGroup, calc, op, value);

    private static async Task<JsonNode> ScreenAsync(
        InstrumentScreeningService service,
        KpiFilterInput[] filters,
        int pageSize = 50,
        KpiSortInput? sortBy = null) =>
        JsonNode.Parse(await service.ScreenAsync(new InstrumentScreeningRequest(
            KpiFilters: filters, SortBy: sortBy, PageSize: pageSize)))!;

    private static int[] ResultIds(JsonNode result) =>
        result["results"]!.AsArray().Select(item => item!["insId"]!.GetValue<int>()).ToArray();

    [Theory]
    [InlineData("lt", 10, new[] { 1 })]
    [InlineData("lte", 10, new[] { 1, 2, 6 })]
    [InlineData("gt", 10, new[] { 3 })]
    [InlineData("gte", 10, new[] { 2, 3, 6 })]
    [InlineData("eq", 10, new[] { 2, 6 })]
    [InlineData("neq", 10, new[] { 1, 3 })]
    public async Task Screen_OneFilter_SupportsEveryOperator(string op, double value, int[] expected)
    {
        var service = CreateService(out _, out _);

        var result = await ScreenAsync(service, [Filter(op: op, value: value)]);

        Assert.Equal(expected, ResultIds(result));
    }

    [Fact]
    public async Task Screen_TwoFilters_UsesAndLogic()
    {
        var service = CreateService(out _, out _);

        var result = await ScreenAsync(service, [Filter(op: "gte", value: 10), Filter(2, "gt", 85)]);

        Assert.Equal([2], ResultIds(result));
    }

    [Fact]
    public async Task Screen_MoreThanTwoFilters_DeduplicatesKpiFetchesButEvaluatesEveryCondition()
    {
        var service = CreateService(out var stub, out _);

        var result = await ScreenAsync(service,
        [
            Filter(op: "gte", value: 10),
            Filter(op: "lte", value: 15),
            Filter(2, "gte", 80),
            Filter(3, "lt", 4)
        ]);

        Assert.Equal([2, 3], ResultIds(result));
        Assert.Equal(3, result["results"]![0]!["kpis"]!.AsArray().Count);
        Assert.Equal(1, stub.RequestUris.Count(uri => uri.AbsolutePath.Contains("/kpis/1/")));
    }

    [Fact]
    public async Task Screen_MetadataListsUseOrWithinDimensionAndAndAcrossDimensions()
    {
        var service = CreateService(out _, out _);
        var request = new InstrumentScreeningRequest(
            CountryIds: [1, 2], MarketIds: [1], SectorIds: [1], BranchIds: [1, 2],
            KpiFilters: [Filter(op: "gte", value: 0)]);

        var result = JsonNode.Parse(await service.ScreenAsync(request))!;

        Assert.Equal([1, 2, 6], ResultIds(result));
    }

    [Fact]
    public async Task Screen_EachMetadataDimensionCanFilterIndividually()
    {
        var service = CreateService(out _, out _);
        var filter = new[] { Filter(3, "gte", 0) };

        var country = JsonNode.Parse(await service.ScreenAsync(new InstrumentScreeningRequest(
            CountryIds: [3], KpiFilters: filter)))!;
        var market = JsonNode.Parse(await service.ScreenAsync(new InstrumentScreeningRequest(
            MarketIds: [3], KpiFilters: filter)))!;
        var sector = JsonNode.Parse(await service.ScreenAsync(new InstrumentScreeningRequest(
            SectorIds: [3], KpiFilters: filter)))!;
        var branch = JsonNode.Parse(await service.ScreenAsync(new InstrumentScreeningRequest(
            BranchIds: [4], KpiFilters: filter)))!;

        Assert.Equal([3], ResultIds(country));
        Assert.Equal([5], ResultIds(market));
        Assert.Equal([5], ResultIds(sector));
        Assert.Equal([4], ResultIds(branch));
    }

    [Fact]
    public async Task Screen_GlobalUniverse_UsesOnlyGlobalEndpoints()
    {
        var service = CreateService(out var stub, out _);

        var result = JsonNode.Parse(await service.ScreenAsync(new InstrumentScreeningRequest(
            Global: true, KpiFilters: [Filter(op: "gt", value: 5)])))!;

        Assert.Equal([101], ResultIds(result));
        Assert.All(stub.RequestUris, uri => Assert.Contains("global", uri.AbsolutePath));
    }

    [Fact]
    public async Task Screen_MissingNullAndStringValuesAreUnevaluable()
    {
        var service = CreateService(out _, out _);

        var result = await ScreenAsync(service, [Filter(op: "gte", value: 0)]);

        Assert.Equal(2, result["unevaluableCount"]!.GetValue<int>());
        Assert.Equal(4, result["totalMatched"]!.GetValue<int>());
        Assert.Equal([1, 2, 3, 6], ResultIds(result));
    }

    [Fact]
    public async Task Screen_InvalidInputsReturnStableErrorCodes()
    {
        var service = CreateService(out _, out _);

        var invalidOperator = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ScreenAsync(new InstrumentScreeningRequest(KpiFilters: [Filter(op: "contains")])));
        var invalidId = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ScreenAsync(new InstrumentScreeningRequest(CountryIds: [0], KpiFilters: [Filter()])));
        var invalidKpi = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ScreenAsync(new InstrumentScreeningRequest(KpiFilters: [Filter(kpiId: 0)])));
        var missingCalculation = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ScreenAsync(new InstrumentScreeningRequest(KpiFilters: [Filter(calc: " ")])));
        var invalidPage = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ScreenAsync(new InstrumentScreeningRequest(KpiFilters: [Filter()], PageSize: 201)));

        Assert.StartsWith("INVALID_REQUEST:", invalidOperator.Message);
        Assert.StartsWith("INVALID_REQUEST:", invalidId.Message);
        Assert.StartsWith("INVALID_REQUEST:", invalidKpi.Message);
        Assert.StartsWith("INVALID_REQUEST:", missingCalculation.Message);
        Assert.StartsWith("INVALID_REQUEST:", invalidPage.Message);
    }

    [Fact]
    public async Task Screen_NoMatchesReturnsEmptyTerminalPage()
    {
        var service = CreateService(out _, out _);

        var result = await ScreenAsync(service, [Filter(op: "gt", value: 1_000)]);

        Assert.Equal(0, result["totalMatched"]!.GetValue<int>());
        Assert.Equal(0, result["returned"]!.GetValue<int>());
        Assert.Empty(result["results"]!.AsArray());
        Assert.Null(result["nextCursor"]);
    }

    [Fact]
    public async Task Screen_BorsdataKpiErrorAbortsWithKpiContext()
    {
        var service = CreateService(out _, out _);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ScreenAsync(new InstrumentScreeningRequest(
                KpiFilters: [Filter(2, calcGroup: "15year", calc: "high")])));

        Assert.StartsWith("KPI_FETCH_FAILED:", error.Message);
        Assert.Contains("2/15year/high", error.Message);
        Assert.Contains("Invalid KPI combination", error.Message);
    }

    [Fact]
    public async Task Screen_InvalidCatalogCombinationIsRejectedBeforeAnyApiCall()
    {
        var service = CreateService(out var stub, out _);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ScreenAsync(new InstrumentScreeningRequest(
                KpiFilters: [Filter(97, calcGroup: "year", calc: "cagr5y")])));

        Assert.StartsWith("INVALID_REQUEST:", error.Message);
        Assert.Contains("Invalid KPI combination 97/year/cagr5y", error.Message);
        Assert.Contains("list_kpi_screener_options", error.Message);
        Assert.Equal(0, stub.CallCount);
    }

    [Theory]
    [InlineData("asc", new[] { 1, 2, 6, 3 })]
    [InlineData("desc", new[] { 3, 2, 6, 1 })]
    public async Task Screen_KpiSortIsDeterministicAndUsesInsIdAsTieBreaker(string direction, int[] expected)
    {
        var service = CreateService(out _, out _);
        var sort = new KpiSortInput(1, "last", "latest", direction);

        var result = await ScreenAsync(service, [Filter(op: "gte", value: 0)], sortBy: sort);

        Assert.Equal(expected, ResultIds(result));
    }

    [Fact]
    public async Task Screen_PaginationReturnsSnapshotWithoutAdditionalApiCalls()
    {
        var service = CreateService(out var stub, out _);
        var first = await ScreenAsync(service, [Filter(op: "gte", value: 0)], pageSize: 2);
        var callsAfterScreening = stub.CallCount;
        var allIds = ResultIds(first).ToList();
        var cursor = first["nextCursor"]!.GetValue<string>();

        var second = JsonNode.Parse(await service.ScreenAsync(new InstrumentScreeningRequest(Cursor: cursor)))!;
        allIds.AddRange(ResultIds(second));

        Assert.Equal([1, 2, 3, 6], allIds);
        Assert.Equal(allIds.Count, allIds.Distinct().Count());
        Assert.Equal(4, first["totalMatched"]!.GetValue<int>());
        Assert.Equal(4, second["totalMatched"]!.GetValue<int>());
        Assert.Null(second["nextCursor"]);
        Assert.Equal(callsAfterScreening, stub.CallCount);
    }

    [Fact]
    public async Task Screen_CursorMustBeValidUnexpiredAndUsedAlone()
    {
        var service = CreateService(out _, out var timeProvider);
        var first = await ScreenAsync(service, [Filter(op: "gte", value: 0)], pageSize: 2);
        var cursor = first["nextCursor"]!.GetValue<string>();

        var combined = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ScreenAsync(new InstrumentScreeningRequest(Cursor: cursor, Global: false)));
        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ScreenAsync(new InstrumentScreeningRequest(Cursor: "not-a-real-cursor")));
        var empty = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ScreenAsync(new InstrumentScreeningRequest(Cursor: " ")));
        timeProvider.Advance(TimeSpan.FromMinutes(16));
        var expired = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ScreenAsync(new InstrumentScreeningRequest(Cursor: cursor)));

        Assert.StartsWith("INVALID_REQUEST:", combined.Message);
        Assert.StartsWith("CURSOR_EXPIRED:", unknown.Message);
        Assert.StartsWith("CURSOR_EXPIRED:", empty.Message);
        Assert.StartsWith("CURSOR_EXPIRED:", expired.Message);
    }

    [Fact]
    public async Task Screen_ConcurrentSnapshotsDoNotMixResults()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var firstService = CreateService(out _, out _, cache);
        var secondService = CreateService(out _, out _, cache);

        var firstTask = ScreenAsync(firstService, [Filter(op: "gte", value: 10)], pageSize: 1);
        var secondTask = ScreenAsync(secondService, [Filter(op: "lt", value: 10)], pageSize: 1);
        var pages = await Task.WhenAll(firstTask, secondTask);

        var firstNext = JsonNode.Parse(await firstService.ScreenAsync(new InstrumentScreeningRequest(
            Cursor: pages[0]["nextCursor"]!.GetValue<string>())))!;
        Assert.Equal([2], ResultIds(pages[0]));
        Assert.Equal([3], ResultIds(firstNext));
        Assert.Equal([1], ResultIds(pages[1]));
        Assert.Null(pages[1]["nextCursor"]);
    }

    [Fact]
    public async Task ScreeningToolDelegatesToServiceAndReturnsStructuredJson()
    {
        var service = CreateService(out _, out _);

        var json = await ScreeningTools.ScreenInstruments(
            service, kpiFilters: [Filter(op: "eq", value: 5)], pageSize: 10);
        var result = JsonNode.Parse(json)!;

        Assert.Equal(1, result["totalMatched"]!.GetValue<int>());
        Assert.Equal("AAA", result["results"]![0]!["ticker"]!.GetValue<string>());
    }

    [Fact]
    public async Task Screen_TenThousandMatchesStillReturnsOnlyConfiguredMaximumPage()
    {
        const int count = 10_000;
        var instruments = new JsonObject
        {
            ["instruments"] = new JsonArray(Enumerable.Range(1, count).Select(id => (JsonNode)new JsonObject
            {
                ["insId"] = id, ["name"] = $"Company {id}", ["ticker"] = $"C{id}"
            }).ToArray())
        }.ToJsonString();
        var values = new JsonObject
        {
            ["values"] = new JsonArray(Enumerable.Range(1, count).Select(id => (JsonNode)new JsonObject
            {
                ["i"] = id, ["n"] = 1.0, ["s"] = null
            }).ToArray())
        }.ToJsonString();
        var handler = new StaticResponsesHandler(instruments, values);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var client = new BorsdataApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://apiservice.borsdata.se/v1/") }, cache);
        var service = new InstrumentScreeningService(
            client, cache, Options.Create(new ScreeningOptions()), TimeProvider.System,
            new KpiScreenerCatalog());

        var result = JsonNode.Parse(await service.ScreenAsync(new InstrumentScreeningRequest(
            KpiFilters: [Filter()], PageSize: 200)))!;

        Assert.Equal(count, result["totalMatched"]!.GetValue<int>());
        Assert.Equal(200, result["returned"]!.GetValue<int>());
        Assert.Equal(200, result["results"]!.AsArray().Count);
        Assert.NotNull(result["nextCursor"]);
    }

    private sealed class StaticResponsesHandler(string instruments, string values) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    request.RequestUri!.AbsolutePath.Contains("/kpis/") ? values : instruments,
                    Encoding.UTF8,
                    "application/json")
            });
    }
}
