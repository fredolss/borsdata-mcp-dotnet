using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BorsdataMcp.Tools;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace BorsdataMcp.Tests;

public class MarketDataToolsTests
{
    private static readonly KpiHistoryCatalog HistoryCatalog = new();

    // Börsdata wraps both responses in envelope objects, confirmed against the live API.
    private const string InstrumentsFixture = """
    { "instruments": [
      { "insId": 1, "name": "Volvo B", "ticker": "VOLV B", "isin": "SE0000115446", "marketId": 1, "countryId": 1, "sectorId": 1, "branchId": 1 },
      { "insId": 2, "name": "Ericsson B", "ticker": "ERIC B", "isin": "SE0000108656", "marketId": 1, "countryId": 1, "sectorId": 2, "branchId": 2 },
      { "insId": 3, "name": "Nordea Bank", "ticker": "NDA SE", "isin": "FI4000297767", "marketId": 2, "countryId": 2, "sectorId": 3, "branchId": 3 },
      { "insId": 4, "name": "Investor B", "ticker": "INVE B", "isin": "SE0000107419", "marketId": 1, "countryId": 1, "sectorId": 4, "branchId": 4 }
    ] }
    """;

    private const string KpiListFixture = """
    { "kpiId": 2, "group": "last", "calculation": "latest", "values": [
      { "i": 1, "n": 14.5, "s": null },
      { "i": 2, "n": 20.3, "s": null },
      { "i": 3, "n": 8.1, "s": null },
      { "i": 4, "n": null, "s": "N/A" },
      { "i": 999, "n": 5.0, "s": null }
    ] }
    """;

    // Confirmed live against the real API: y = year, p = period, v = value.
    private const string KpiHistoryFixture = """
    { "kpiId": 2, "reportTime": "year", "priceValue": "mean", "values": [
      { "y": 2026, "p": 2, "v": 19.31 },
      { "y": 2025, "p": 5, "v": 19.10 }
    ] }
    """;

    // Matches Börsdata's KpisHistoryArrayRespV1/KpisHistoryCompV1 schema (confirmed against the
    // OpenAPI spec at apidoc.borsdata.se/swagger/v1/swagger.json) — the bulk "for a list of
    // instruments" counterpart to KpiHistoryFixture above, keyed by "kpisList" with "instrument"
    // per entry (not "insId"), and an optional per-entry "error" field.
    private const string KpiHistoryArrayFixture = """
    { "kpiId": 2, "reportTime": "year", "priceValue": "mean", "kpisList": [
      { "instrument": 1, "values": [ { "y": 2026, "p": 2, "v": 14.5 } ] },
      { "instrument": 3, "values": [ { "y": 2026, "p": 2, "v": 8.1 } ] }
    ] }
    """;

    // Matches Börsdata's ReportsCompoundRespV1 schema — no envelope wrapper, "instrument" plus the
    // three report-type arrays directly at the top level.
    private const string ReportsCompoundFixture = """
    { "instrument": 1, "reportsYear": [ { "year": 2025, "revenues": 100.0 } ], "reportsQuarter": [], "reportsR12": [] }
    """;

    // Matches Börsdata's ReportsArrayRespV1/ReportsCombineRespV1 schema — keyed by "reportList",
    // each entry has "instrument" (not "insId") and an optional "error" field, confirmed against
    // the OpenAPI spec.
    private const string ReportsArrayFixture = """
    { "reportList": [
      { "instrument": 1, "reportsYear": [ { "year": 2025, "revenues": 100.0 } ], "reportsQuarter": [], "reportsR12": [] },
      { "instrument": 999, "error": "Instrument not found" }
    ] }
    """;

    // Confirmed live: this is the actual "summary" endpoint — every KPI for one instrument across
    // multiple periods, keyed by "KpiId" (PascalCase, unlike every other endpoint's "kpiId"), under
    // an "instrument" field (not "insId"/"instrumentId" like elsewhere).
    private const string KpiSummaryFixture = """
    { "instrument": 236, "reportType": "year", "kpis": [
      { "KpiId": 1, "values": [ { "y": 2026, "p": 2, "v": 3.818 }, { "y": 2025, "p": 5, "v": 4.016 } ] },
      { "KpiId": 2, "values": [ { "y": 2026, "p": 2, "v": 19.314 }, { "y": 2025, "p": 5, "v": 19.097 } ] }
    ] }
    """;

    // Confirmed live: same envelope/field shape (i, d, h, l, c, o, v) for both stockprices/last and
    // stockprices/date, and neither accepts instList server-side — every instrument comes back
    // regardless, so filtering has to happen client-side.
    private const string StockPricesFixture = """
    { "stockPricesList": [
      { "i": 1, "d": "2026-09-11", "h": 205.4, "l": 199.7, "c": 200.2, "o": 205.2, "v": 534031 },
      { "i": 2, "d": "2026-09-11", "h": 941.6, "l": 927.0, "c": 933.8, "o": 932.4, "v": 242781 },
      { "i": 999, "d": "2026-09-11", "h": 4.5, "l": 4.2, "c": 4.3, "o": 4.5, "v": 41717 }
    ] }
    """;

    // Single-instrument stockprices envelope — a different shape than the bulk stockprices/last|date
    // fixture above: no per-entry "i" field (implied by the single top-level "instrument" field).
    // Confirmed live: chronological ascending order (oldest first). Deliberately kept small (4
    // entries) here since the real endpoint's own maxCount means "max year count", not a row
    // limit (see BorsdataApiClient.GetStockPricesAsync) — client-side capping is what actually
    // bounds the row count, and these tests exercise that behavior against this fixture.
    private const string SingleInstrumentStockPricesFixture = """
    { "instrument": 352, "stockPricesList": [
      { "d": "2026-09-08", "h": 100.0, "l": 95.0, "c": 98.0, "o": 96.0, "v": 1000 },
      { "d": "2026-09-09", "h": 101.0, "l": 96.0, "c": 99.0, "o": 98.0, "v": 1100 },
      { "d": "2026-09-10", "h": 102.0, "l": 97.0, "c": 100.0, "o": 99.0, "v": 1200 },
      { "d": "2026-09-11", "h": 103.0, "l": 98.0, "c": 101.0, "o": 100.0, "v": 1300 }
    ] }
    """;

    // Confirmed live: global insIds start well above the Nordic range (real example: 10054).
    private const string GlobalInstrumentsFixture = """
    { "instruments": [
      { "insId": 10054, "name": "FG Nexus Inc", "ticker": "FGNX", "isin": "US00000FGNX0", "marketId": 33, "countryId": 5, "sectorId": 9, "branchId": 9 }
    ] }
    """;

    private const string GlobalKpiListFixture = """
    { "kpiId": 2, "group": "last", "calculation": "latest", "values": [
      { "i": 10054, "n": 0.84, "s": null }
    ] }
    """;

    private const string GlobalStockPricesFixture = """
    { "stockPricesList": [
      { "i": 10054, "d": "2026-09-11", "h": 118.7, "l": 112.5, "c": 113.7, "o": 118.7, "v": 585 }
    ] }
    """;

    private sealed class RoutingStubHandler : DelegatingHandler
    {
        private readonly List<Uri> requestUris = [];
        public int CallCount => requestUris.Count;
        public Uri LastRequestUri => requestUris[^1];
        public IReadOnlyList<Uri> RequestUris => requestUris;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requestUris.Add(request.RequestUri!);
            var path = request.RequestUri!.AbsolutePath;
            var body =
                // Bulk KPI history is "instruments/kpis/{kpiId}/.../history" (no insId segment),
                // unlike the single-instrument "instruments/{insId}/kpis/{kpiId}/.../history" —
                // must be checked before the generic "/history" branch below.
                path.Contains("instruments/kpis/") && path.Contains("/history") ? KpiHistoryArrayFixture
                : path.Contains("/history") ? KpiHistoryFixture
                : path.Contains("/summary") ? KpiSummaryFixture
                : path.Contains("instruments/global/kpis") ? GlobalKpiListFixture
                : path.Contains("stockprices/global") ? GlobalStockPricesFixture
                : path.Contains("instruments/global") ? GlobalInstrumentsFixture
                : path.Contains("/kpis/") ? KpiListFixture
                // Bulk reports is "instruments/reports" (no insId segment); compound (all report
                // types for one instrument) is "instruments/{insId}/reports" — both end in
                // "/reports", so the more specific bulk path is checked first.
                : path.EndsWith("/instruments/reports") ? ReportsArrayFixture
                : path.EndsWith("/reports") ? ReportsCompoundFixture
                // Single-instrument endpoint is "instruments/{id}/stockprices" — no trailing
                // slash/segment after "stockprices", unlike the bulk "stockprices/last" and
                // "stockprices/date" endpoints matched below, so EndsWith distinguishes them.
                : path.EndsWith("/stockprices") ? SingleInstrumentStockPricesFixture
                : path.Contains("/stockprices/") ? StockPricesFixture
                : InstrumentsFixture;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    private static BorsdataApiClient CreateClient(out RoutingStubHandler stub)
    {
        stub = new RoutingStubHandler();
        var httpClient = new HttpClient(stub) { BaseAddress = new Uri("https://apiservice.borsdata.se/v1/") };
        return new BorsdataApiClient(httpClient, new MemoryCache(new MemoryCacheOptions()));
    }

    private static JsonNode AsJson(StockPricePageResult result) =>
        JsonSerializer.SerializeToNode(result)!;

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan duration) => Now = Now.Add(duration);
    }

    [Fact]
    public async Task GetKpiListScreener_NoFilters_ReturnsAllInBorsdatasOwnOrder_EnrichesWithTickerAndName()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await MarketDataTools.GetKpiListScreener(client, kpiId: 2, calcGroup: "last", calc: "latest"))!;

        // No client-side sorting/filtering/capping — exactly Börsdata's own fixture order.
        var insIds = result["values"]!.AsArray().Select(v => v!["insId"]!.GetValue<int>()).ToList();
        Assert.Equal([1, 2, 3, 4, 999], insIds);

        var unknown = result["values"]![4]!;
        Assert.Equal(999, unknown["insId"]!.GetValue<int>());
        Assert.Equal(5.0, unknown["value"]!.GetValue<double>());
        Assert.Null(unknown["name"]);

        var nordea = result["values"]!.AsArray().Single(v => v!["insId"]!.GetValue<int>() == 3)!;
        Assert.Equal("Nordea Bank", nordea["name"]!.GetValue<string>());
        Assert.Equal("NDA SE", nordea["ticker"]!.GetValue<string>());
        Assert.Equal(8.1, nordea["value"]!.GetValue<double>());
    }

    [Fact]
    public async Task GetKpiListScreener_UnknownInsId_OmitsTickerAndNameButKeepsValue()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await MarketDataTools.GetKpiListScreener(client, kpiId: 2, calcGroup: "last", calc: "latest"))!;

        var unknown = result["values"]!.AsArray().Single(v => v!["insId"]!.GetValue<int>() == 999);
        Assert.Null(unknown!["name"]);
        Assert.Equal(5.0, unknown["value"]!.GetValue<double>());
    }

    [Fact]
    public async Task GetKpiListScreener_StringOnlyValue_IsSurfacedAsStringValue()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await MarketDataTools.GetKpiListScreener(client, kpiId: 2, calcGroup: "last", calc: "latest"))!;

        var stringEntry = result["values"]!.AsArray().Single(v => v!["insId"]!.GetValue<int>() == 4);
        Assert.Equal("N/A", stringEntry!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetKpiListScreener_UsesCachedInstruments_DoesNotRefetchOnSecondCall()
    {
        var client = CreateClient(out var stub);

        await MarketDataTools.GetKpiListScreener(client, kpiId: 2, calcGroup: "last", calc: "latest");
        await MarketDataTools.GetKpiListScreener(client, kpiId: 2, calcGroup: "last", calc: "latest");

        // 1 KPI-list fetch (now cached with a short TTL — values only change once per trading day)
        // + 1 instruments fetch (cached) = 2 total across both calls.
        Assert.Equal(2, stub.CallCount);
    }

    [Fact]
    public async Task GetKpiListScreener_NeverRequestsGlobalUrls()
    {
        var client = CreateClient(out var stub);

        await MarketDataTools.GetKpiListScreener(client, kpiId: 2, calcGroup: "last", calc: "latest");

        Assert.All(stub.RequestUris, u => Assert.DoesNotContain("/global/", u.AbsolutePath));
    }

    [Fact]
    public async Task GetGlobalKpiListScreener_UsesGlobalDataSourceAndEnrichment()
    {
        var client = CreateClient(out var stub);

        var result = JsonNode.Parse(await MarketDataTools.GetGlobalKpiListScreener(
            client, kpiId: 2, calcGroup: "last", calc: "latest"))!;

        Assert.Contains(stub.RequestUris, u => u.AbsolutePath == "/v1/instruments/global/kpis/2/last/latest");
        Assert.Contains(stub.RequestUris, u => u.AbsolutePath == "/v1/instruments/global");
        var entry = result["values"]![0]!;
        Assert.Equal(10054, entry["insId"]!.GetValue<int>());
        Assert.Equal("FG Nexus Inc", entry["name"]!.GetValue<string>());
        Assert.Equal("FGNX", entry["ticker"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetGlobalKpiListScreener_DoesNotAlsoFetchNordicData()
    {
        var client = CreateClient(out var stub);

        await MarketDataTools.GetGlobalKpiListScreener(client, kpiId: 2, calcGroup: "last", calc: "latest");

        // Just the global values-fetch + the global instruments-fetch — no Nordic fetch at all.
        Assert.Equal(2, stub.CallCount);
        Assert.All(stub.RequestUris, u => Assert.Contains("global", u.AbsolutePath));
    }

    [Fact]
    public async Task GetKpiHistory_BuildsCorrectPathAndQuery_AndReturnsRawResponse()
    {
        var client = CreateClient(out var stub);

        var result = JsonNode.Parse(await MarketDataTools.GetKpiHistory(
            client, HistoryCatalog, instrumentId: 236, kpiId: 2, reportType: "year", priceType: "mean", maxCount: 5))!;

        Assert.Equal("/v1/instruments/236/kpis/2/year/mean/history", stub.LastRequestUri!.AbsolutePath);
        Assert.Equal("maxCount=5", stub.LastRequestUri.Query.TrimStart('?'));
        Assert.Equal(2, result["values"]!.AsArray().Count);
        Assert.Equal(19.31, result["values"]![0]!["v"]!.GetValue<double>());
    }

    [Fact]
    public async Task GetKpiHistoryArray_BuildsCorrectPathAndQuery_AndReturnsRawResponse()
    {
        var client = CreateClient(out var stub);

        var result = JsonNode.Parse(await MarketDataTools.GetKpiHistoryArray(
            client, HistoryCatalog, kpiId: 2, reportType: "year", priceType: "mean", instrumentIds: "1,3", maxCount: 5))!;

        Assert.Equal("/v1/instruments/kpis/2/year/mean/history", stub.LastRequestUri!.AbsolutePath);
        Assert.Equal("instList=1%2C3&maxCount=5", stub.LastRequestUri.Query.TrimStart('?'));
        var kpisList = result["kpisList"]!.AsArray();
        Assert.Equal(2, kpisList.Count);
        Assert.Equal(1, kpisList[0]!["instrument"]!.GetValue<int>());
        Assert.Equal(14.5, kpisList[0]!["values"]![0]!["v"]!.GetValue<double>());
    }

    [Fact]
    public async Task GetReportsCompound_BuildsCorrectPathAndQuery_AndReturnsRawResponse()
    {
        var client = CreateClient(out var stub);

        var result = JsonNode.Parse(await MarketDataTools.GetReportsCompound(
            client, instrumentId: 1, maxYearCount: 5, maxR12QCount: 10, original: true))!;

        Assert.Equal("/v1/instruments/1/reports", stub.LastRequestUri!.AbsolutePath);
        Assert.Equal("maxYearCount=5&maxR12QCount=10&original=1", stub.LastRequestUri.Query.TrimStart('?'));
        Assert.Equal(1, result["instrument"]!.GetValue<int>());
        Assert.Single(result["reportsYear"]!.AsArray());
    }

    [Fact]
    public async Task GetReportsArray_BuildsCorrectPathAndQuery_AndReturnsRawResponse()
    {
        var client = CreateClient(out var stub);

        var result = JsonNode.Parse(await MarketDataTools.GetReportsArray(
            client, instrumentIds: "1,999"))!;

        Assert.Equal("/v1/instruments/reports", stub.LastRequestUri!.AbsolutePath);
        Assert.Equal("instList=1%2C999", stub.LastRequestUri.Query.TrimStart('?'));
        var reportList = result["reportList"]!.AsArray();
        Assert.Equal(2, reportList.Count);
        Assert.Equal(1, reportList[0]!["instrument"]!.GetValue<int>());
        Assert.Equal("Instrument not found", reportList[1]!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetKpiHistory_OmittingMaxCount_SendsNoQueryString()
    {
        var client = CreateClient(out var stub);

        await MarketDataTools.GetKpiHistory(
            client, HistoryCatalog, instrumentId: 236, kpiId: 2, reportType: "year", priceType: "mean");

        Assert.Equal(string.Empty, stub.LastRequestUri!.Query);
    }

    [Fact]
    public async Task GetKpiHistoryArray_InvalidCombinationIsRejectedBeforeApiCall()
    {
        var client = CreateClient(out var stub);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MarketDataTools.GetKpiHistoryArray(
                client, HistoryCatalog, kpiId: 6, reportType: "year", priceType: "latest",
                instrumentIds: "1,3", maxCount: 5));

        Assert.StartsWith("INVALID_REQUEST:", error.Message);
        Assert.Contains("6/year/latest", error.Message);
        Assert.Contains("year/mean", error.Message);
        Assert.Equal(0, stub.CallCount);
    }

    [Fact]
    public async Task GetKpiScreener_BuildsCorrectPath_AndReturnsRawResponse()
    {
        var client = CreateClient(out var stub);

        var result = JsonNode.Parse(await MarketDataTools.GetKpiScreener(
            client, instrumentId: 1, kpiId: 2, calcGroup: "last", calc: "latest", CancellationToken.None))!;

        Assert.Equal("/v1/instruments/1/kpis/2/last/latest", stub.LastRequestUri!.AbsolutePath);
        Assert.Equal(5, result["values"]!.AsArray().Count);
    }

    [Fact]
    public async Task GetKpiSummary_BuildsCorrectPathAndQuery_AndReturnsRawResponse()
    {
        var client = CreateClient(out var stub);

        var result = JsonNode.Parse(await MarketDataTools.GetKpiSummary(
            client, instrumentId: 236, reportType: "year", maxCount: 2))!;

        Assert.Equal("/v1/instruments/236/kpis/year/summary", stub.LastRequestUri!.AbsolutePath);
        Assert.Equal("maxCount=2", stub.LastRequestUri.Query.TrimStart('?'));
        var kpis = result["kpis"]!.AsArray();
        Assert.Equal(2, kpis.Count);
        var pe = kpis.Single(k => k!["KpiId"]!.GetValue<int>() == 2)!;
        Assert.Equal(19.314, pe["values"]![0]!["v"]!.GetValue<double>());
    }

    [Fact]
    public async Task GetKpiSummary_OmittingMaxCount_SendsNoQueryString()
    {
        var client = CreateClient(out var stub);

        await MarketDataTools.GetKpiSummary(client, instrumentId: 236, reportType: "year");

        Assert.Equal(string.Empty, stub.LastRequestUri!.Query);
    }

    [Fact]
    public async Task GetLatestStockPrices_NoFilters_ReturnsAllAndEnrichesWithTickerAndName()
    {
        var client = CreateClient(out var stub);

        var result = AsJson(await MarketDataTools.GetLatestStockPrices(client, TestServices.CreatePagination()));

        Assert.Contains(stub.RequestUris, u => u.AbsolutePath == "/v1/instruments/stockprices/last");
        Assert.Equal(3, result["totalMatched"]!.GetValue<int>());
        var volvo = result["values"]!.AsArray().Single(v => v!["i"]!.GetValue<int>() == 1)!;
        Assert.Equal("Volvo B", volvo["name"]!.GetValue<string>());
        Assert.Equal("VOLV B", volvo["ticker"]!.GetValue<string>());
        Assert.Equal(200.2, volvo["c"]!.GetValue<double>());
    }

    [Fact]
    public async Task GetLatestStockPrices_FiltersByInstrumentIds()
    {
        var client = CreateClient(out _);

        var result = AsJson(await MarketDataTools.GetLatestStockPrices(
            client, TestServices.CreatePagination(), instrumentIds: "1,2"));

        Assert.Equal(2, result["totalMatched"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("ticker", "asc", new long[] { 2, 1, 999 })]
    [InlineData("ticker", "desc", new long[] { 1, 2, 999 })]
    [InlineData("name", "asc", new long[] { 2, 1, 999 })]
    [InlineData("name", "desc", new long[] { 1, 2, 999 })]
    public async Task GetLatestStockPrices_SortsByTickerOrName(string sortBy, string direction, long[] expected)
    {
        var client = CreateClient(out _);

        var result = await MarketDataTools.GetLatestStockPrices(
            client, TestServices.CreatePagination(), sortBy: sortBy, sortDirection: direction);

        Assert.Equal(expected, result.Values.Select(value => value.InstrumentId));
    }

    [Fact]
    public async Task GetLatestStockPrices_PaginatesSnapshotWithoutDuplicatesOrAdditionalApiCalls()
    {
        var client = CreateClient(out var stub);
        var pagination = TestServices.CreatePagination();
        var first = await MarketDataTools.GetLatestStockPrices(client, pagination, pageSize: 1);
        var callsAfterFirstPage = stub.CallCount;
        var second = await MarketDataTools.GetLatestStockPrices(
            client, pagination, cursor: first.NextCursor);
        var third = await MarketDataTools.GetLatestStockPrices(
            client, pagination, cursor: second.NextCursor);

        var ids = first.Values.Concat(second.Values).Concat(third.Values)
            .Select(value => value.InstrumentId).ToArray();
        Assert.Equal([2L, 1L, 999L], ids);
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.Null(third.NextCursor);
        Assert.Equal(callsAfterFirstPage, stub.CallCount);
    }

    [Fact]
    public async Task StockPriceCursors_AreToolScopedAndMustBeUsedAlone()
    {
        var client = CreateClient(out _);
        var pagination = TestServices.CreatePagination();
        var latest = await MarketDataTools.GetLatestStockPrices(client, pagination, pageSize: 1);

        var wrongTool = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MarketDataTools.GetStockPricesByDate(client, pagination, cursor: latest.NextCursor));
        var combined = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MarketDataTools.GetLatestStockPrices(client, pagination, cursor: latest.NextCursor, global: false));

        Assert.StartsWith("CURSOR_EXPIRED:", wrongTool.Message);
        Assert.StartsWith("INVALID_REQUEST:", combined.Message);
    }

    [Fact]
    public async Task StockPriceCursor_CannotBeUsedBySearchInstruments()
    {
        var client = CreateClient(out _);
        var pagination = TestServices.CreatePagination();
        var latest = await MarketDataTools.GetLatestStockPrices(client, pagination, pageSize: 1);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ReferenceDataTools.SearchInstruments(client, pagination, cursor: latest.NextCursor));

        Assert.StartsWith("CURSOR_EXPIRED:", error.Message);
    }

    [Fact]
    public async Task StockPriceTools_RejectExpiredCursorInvalidDateAndPageSize()
    {
        var client = CreateClient(out _);
        var time = new MutableTimeProvider(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var pagination = TestServices.CreatePagination(timeProvider: time);
        var first = await MarketDataTools.GetLatestStockPrices(client, pagination, pageSize: 1);

        var badDate = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MarketDataTools.GetStockPricesByDate(client, pagination, date: "09/10/2026"));
        var badPage = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MarketDataTools.GetLatestStockPrices(client, pagination, pageSize: 201));
        time.Advance(TimeSpan.FromMinutes(16));
        var expired = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MarketDataTools.GetLatestStockPrices(client, pagination, cursor: first.NextCursor));

        Assert.StartsWith("INVALID_REQUEST:", badDate.Message);
        Assert.StartsWith("INVALID_REQUEST:", badPage.Message);
        Assert.StartsWith("CURSOR_EXPIRED:", expired.Message);
    }

    [Fact]
    public async Task GetLatestStockPrices_UnknownInsId_OmitsTickerAndNameButKeepsData()
    {
        var client = CreateClient(out _);

        var result = AsJson(await MarketDataTools.GetLatestStockPrices(client, TestServices.CreatePagination()));

        var unknown = result["values"]!.AsArray().Single(v => v!["i"]!.GetValue<int>() == 999)!;
        Assert.Null(unknown["name"]);
        Assert.Equal(4.3, unknown["c"]!.GetValue<double>());
    }

    [Fact]
    public async Task GetLatestStockPrices_PageSizeCapsReturned_ButNotTotalMatched()
    {
        var client = CreateClient(out _);

        var result = AsJson(await MarketDataTools.GetLatestStockPrices(
            client, TestServices.CreatePagination(), pageSize: 1));

        Assert.Equal(3, result["totalMatched"]!.GetValue<int>());
        Assert.Equal(1, result["returned"]!.GetValue<int>());
    }

    [Fact]
    public async Task GetStockPrices_NoParams_ReturnsRawResponseWithNoQueryString()
    {
        var client = CreateClient(out var stub);

        var result = JsonNode.Parse(await MarketDataTools.GetStockPrices(client, instrumentId: 352))!;

        Assert.Equal("/v1/instruments/352/stockprices", stub.LastRequestUri.AbsolutePath);
        Assert.Equal("", stub.LastRequestUri.Query);
        Assert.Equal(4, result["stockPricesList"]!.AsArray().Count);
    }

    [Fact]
    public async Task GetStockPrices_MaxCount_IsPassedThroughAsBorsdatasOwnYearCountParam()
    {
        // maxCount here is Börsdata's own "Max Year Count. Max 20" (confirmed live against the
        // real Swagger-documented endpoint — see BorsdataApiClient.GetStockPricesAsync), not a
        // row/entry limit, so it's sent straight through rather than applied client-side.
        var client = CreateClient(out var stub);

        await MarketDataTools.GetStockPrices(client, instrumentId: 352, maxCount: 5);

        var request = stub.RequestUris.Single(u => u.AbsolutePath == "/v1/instruments/352/stockprices");
        Assert.Equal("maxCount=5", request.Query.TrimStart('?'));
    }

    [Fact]
    public async Task GetStockPrices_BuildsCorrectQuery_WithFromToAndMaxCount()
    {
        var client = CreateClient(out var stub);

        await MarketDataTools.GetStockPrices(client, instrumentId: 352, from: "2026-01-01", to: "2026-02-01", maxCount: 5);

        var request = stub.RequestUris.Single(u => u.AbsolutePath == "/v1/instruments/352/stockprices");
        Assert.Equal("from=2026-01-01&to=2026-02-01&maxCount=5", request.Query.TrimStart('?'));
    }

    [Fact]
    public async Task GetStockPricesByDate_BuildsCorrectPathAndQuery()
    {
        var client = CreateClient(out var stub);

        var result = AsJson(await MarketDataTools.GetStockPricesByDate(
            client, TestServices.CreatePagination(), date: "2026-09-10", instrumentIds: "1"));

        var dateRequest = stub.RequestUris.Single(u => u.AbsolutePath == "/v1/instruments/stockprices/date");
        Assert.Equal("date=2026-09-10", dateRequest.Query.TrimStart('?'));
        Assert.Equal(1, result["totalMatched"]!.GetValue<int>());
    }

    [Fact]
    public async Task GetStockPricesByDate_CursorKeepsHistoricalSnapshotAndDate()
    {
        var client = CreateClient(out var stub);
        var pagination = TestServices.CreatePagination();
        var first = await MarketDataTools.GetStockPricesByDate(
            client, pagination, date: "2026-09-10", pageSize: 1);
        var callsAfterFirstPage = stub.CallCount;

        var second = await MarketDataTools.GetStockPricesByDate(
            client, pagination, cursor: first.NextCursor);

        Assert.All(first.Values.Concat(second.Values), value => Assert.Equal("2026-09-11", value.Date));
        Assert.Equal(callsAfterFirstPage, stub.CallCount);
        Assert.Contains(stub.RequestUris, uri => uri.Query.Contains("date=2026-09-10"));
    }

    [Fact]
    public async Task StockPriceResult_ContainsActualCompactFieldsAndOmitsTerminalCursor()
    {
        var client = CreateClient(out _);

        var result = await MarketDataTools.GetLatestStockPrices(client, TestServices.CreatePagination());
        var json = AsJson(result);
        var value = json["values"]!.AsArray().Single(item => item!["i"]!.GetValue<long>() == 1)!;

        Assert.Equal("2026-09-11", value["d"]!.GetValue<string>());
        Assert.Equal(205.4, value["h"]!.GetValue<double>());
        Assert.Equal(199.7, value["l"]!.GetValue<double>());
        Assert.Equal(200.2, value["c"]!.GetValue<double>());
        Assert.Equal(205.2, value["o"]!.GetValue<double>());
        Assert.Equal(534031, value["v"]!.GetValue<long>());
        Assert.Equal("VOLV B", value["ticker"]!.GetValue<string>());
        Assert.Equal("Volvo B", value["name"]!.GetValue<string>());
        Assert.Null(json["nextCursor"]);
    }

    [Fact]
    public void StockPriceTools_AdvertiseStructuredOutputSchemaWithCompactFields()
    {
        var client = CreateClient(out _);
        var pagination = TestServices.CreatePagination();
        using var services = new ServiceCollection()
            .AddSingleton(client)
            .AddSingleton(pagination)
            .BuildServiceProvider();

        foreach (var methodName in new[] { nameof(MarketDataTools.GetLatestStockPrices), nameof(MarketDataTools.GetStockPricesByDate) })
        {
            var method = typeof(MarketDataTools).GetMethod(methodName)!;
            var tool = McpServerTool.Create(
                method, target: null, options: new McpServerToolCreateOptions { Services = services });
            var inputProperties = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!["properties"]!;
            var schema = JsonNode.Parse(tool.ProtocolTool.OutputSchema!.Value.GetRawText())!;
            var properties = schema["properties"]!;
            var itemSchema = properties["values"]!["items"]!;
            var itemReference = itemSchema["$ref"]?.GetValue<string>();
            var itemProperties = itemReference is null
                ? itemSchema["properties"]!
                : schema["$defs"]![itemReference.Split('/')[^1]]!["properties"]!;

            Assert.NotNull(properties["totalMatched"]);
            Assert.NotNull(properties["returned"]);
            Assert.NotNull(properties["nextCursor"]);
            Assert.DoesNotContain("nextCursor", schema["required"]!.AsArray().Select(item => item!.GetValue<string>()));
            foreach (var field in new[] { "i", "ticker", "name", "d", "h", "l", "c", "o", "v" })
                Assert.NotNull(itemProperties[field]);
            Assert.Contains("integer", itemProperties["i"]!["type"]!.ToJsonString());
            Assert.Contains("number", itemProperties["c"]!["type"]!.ToJsonString());
            Assert.Contains("integer", itemProperties["v"]!["type"]!.ToJsonString());
            Assert.Contains("null", itemProperties["v"]!["type"]!.ToJsonString());
            Assert.Contains("string", itemProperties["d"]!["type"]!.ToJsonString());
            Assert.Contains("null", itemProperties["d"]!["type"]!.ToJsonString());
            Assert.Contains("null", itemProperties["h"]!["type"]!.ToJsonString());
            Assert.Contains("null", itemProperties["l"]!["type"]!.ToJsonString());
            Assert.Contains("null", itemProperties["o"]!["type"]!.ToJsonString());
            Assert.Contains("not necessarily a real-time quote", itemProperties["c"]!["description"]!.GetValue<string>());
            Assert.Null(itemProperties["currency"]);
            Assert.Null(itemProperties["close"]);
            Assert.NotNull(inputProperties["cursor"]);
            Assert.NotNull(inputProperties["pageSize"]);
            Assert.NotNull(inputProperties["sortBy"]);
            Assert.NotNull(inputProperties["sortDirection"]);
            Assert.Null(inputProperties["client"]);
            Assert.Null(inputProperties["pagination"]);
            Assert.Null(inputProperties["maxCount"]);
        }
    }

    [Fact]
    public async Task GetLatestStockPrices_IsCached_DoesNotRefetchOnSecondCall()
    {
        var client = CreateClient(out var stub);

        await MarketDataTools.GetLatestStockPrices(client, TestServices.CreatePagination());
        await MarketDataTools.GetLatestStockPrices(client, TestServices.CreatePagination());

        // 1 latest-prices fetch (now cached — only changes once per trading day) + 1 instruments
        // fetch (also cached) = 2 total across both calls.
        Assert.Equal(2, stub.CallCount);
    }

    [Fact]
    public async Task GetStockPricesByDate_IsCached_PerDate()
    {
        var client = CreateClient(out var stub);

        await MarketDataTools.GetStockPricesByDate(client, TestServices.CreatePagination(), date: "2026-09-10");
        await MarketDataTools.GetStockPricesByDate(client, TestServices.CreatePagination(), date: "2026-09-10");
        await MarketDataTools.GetStockPricesByDate(client, TestServices.CreatePagination(), date: "2026-09-11");

        // Two distinct dates -> two distinct cache entries for the prices fetch, plus one shared,
        // cached instruments fetch: 2 (prices, one per date) + 1 (instruments) = 3, not 5.
        Assert.Equal(3, stub.CallCount);
    }

    [Fact]
    public async Task GetLatestStockPrices_GlobalFalse_NeverRequestsGlobalUrls()
    {
        var client = CreateClient(out var stub);

        await MarketDataTools.GetLatestStockPrices(client, TestServices.CreatePagination());

        Assert.All(stub.RequestUris, u => Assert.DoesNotContain("/global", u.AbsolutePath));
    }

    [Fact]
    public async Task GetLatestStockPrices_GlobalTrue_SwitchesDataSourceAndEnrichment()
    {
        var client = CreateClient(out var stub);

        var result = AsJson(await MarketDataTools.GetLatestStockPrices(
            client, TestServices.CreatePagination(), global: true));

        Assert.Contains(stub.RequestUris, u => u.AbsolutePath == "/v1/instruments/stockprices/global/last");
        Assert.Contains(stub.RequestUris, u => u.AbsolutePath == "/v1/instruments/global");
        var entry = result["values"]![0]!;
        Assert.Equal(10054, entry["i"]!.GetValue<int>());
        Assert.Equal("FG Nexus Inc", entry["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetStockPricesByDate_GlobalTrue_SwitchesDataSourceAndEnrichment()
    {
        var client = CreateClient(out var stub);

        var result = AsJson(await MarketDataTools.GetStockPricesByDate(
            client, TestServices.CreatePagination(), date: "2026-09-11", global: true));

        Assert.Contains(stub.RequestUris, u => u.AbsolutePath == "/v1/instruments/stockprices/global/date");
        Assert.Contains(stub.RequestUris, u => u.AbsolutePath == "/v1/instruments/global");
        var entry = result["values"]![0]!;
        Assert.Equal("FG Nexus Inc", entry["name"]!.GetValue<string>());
    }
}
