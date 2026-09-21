using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BorsdataMcp.Tools;
using Microsoft.Extensions.Caching.Memory;

namespace BorsdataMcp.Tests;

public class ReferenceDataToolsTests
{
    // Börsdata wraps the array in an envelope object keyed by endpoint name, e.g.
    // { "instruments": [ ... ] } — confirmed against the live API, not a bare array.
    private const string InstrumentsFixture = """
    { "instruments": [
      { "insId": 1, "name": "Volvo B", "ticker": "VOLV B", "isin": "SE0000115446", "marketId": 1, "countryId": 1, "sectorId": 1, "branchId": 1 },
      { "insId": 2, "name": "Volvo A", "ticker": "VOLV A", "isin": "SE0000115420", "marketId": 1, "countryId": 1, "sectorId": 1, "branchId": 1 },
      { "insId": 3, "name": "Ericsson B", "ticker": "ERIC B", "isin": "SE0000108656", "marketId": 1, "countryId": 1, "sectorId": 2, "branchId": 2 },
      { "insId": 4, "name": "Nordea Bank", "ticker": "NDA SE", "isin": "FI4000297767", "marketId": 2, "countryId": 2, "sectorId": 3, "branchId": 3 },
      { "insId": 5, "name": "Investor B", "ticker": "INVE B", "isin": "SE0000107419", "marketId": 1, "countryId": 1, "sectorId": 4, "branchId": 4 },
      { "insId": 6, "name": "Atlas Copco A", "ticker": "ATCO A", "isin": "SE0011166610", "marketId": 1, "countryId": 1, "sectorId": 1, "branchId": 1 }
    ] }
    """;

    // Börsdata's KPI metadata endpoint uses a differently-named envelope key than the other
    // reference-data endpoints — confirmed live: { "kpiHistoryMetadatas": [ ... ] }.
    private const string KpiMetadataFixture = """
    { "kpiHistoryMetadatas": [
      { "kpiId": 1, "nameSv": "Direktavkastning", "nameEn": "Dividend Yield", "format": "%", "isString": false },
      { "kpiId": 2, "nameSv": "P/E", "nameEn": "P/E", "format": null, "isString": false }
    ] }
    """;

    // Confirmed live: this endpoint uses "instrumentId" as the field name, not "insId" like every
    // other endpoint (including the InstrumentsFixture above).
    private const string StockSplitsFixture = """
    { "stockSplitList": [
      { "instrumentId": 1, "splitType": "RS", "ratio": "1:100", "splitDate": "2025-09-24T00:00:00" },
      { "instrumentId": 999, "splitType": "S", "ratio": "4:1", "splitDate": "2020-01-01T00:00:00" }
    ] }
    """;

    private const string ReportMetadataFixture = """
    { "reportMetadatas": [
      { "reportPropery": "cash_And_Equivalents", "nameSv": "Kassa/Bank", "nameEn": "Cash and equivalents", "format": "MCURR" },
      { "reportPropery": "broken_Fiscal_Year", "nameSv": "Brutet räkenskapsår", "nameEn": "Broken fiscal year", "format": null }
    ] }
    """;

    // Confirmed live: unlike every other reference-data endpoint, this path has no "instruments/"
    // prefix — it's just "/v1/translationmetadata".
    private const string TranslationMetadataFixture = """
    { "translationMetadatas": [
      { "nameSv": "Finans & Fastighet", "nameEn": "Financials", "translationKey": "L_SECTOR_1" },
      { "nameSv": "Energi", "nameEn": "Energy", "translationKey": "L_SECTOR_3" }
    ] }
    """;

    private const string InstrumentsUpdatedFixture = """
    { "instruments": [
      { "insId": 1, "updatedAt": "2026-09-10T12:00:00" },
      { "insId": 2, "updatedAt": "2026-09-11T12:00:00" },
      { "insId": 999, "updatedAt": "2026-09-12T06:00:00" }
    ] }
    """;

    // Confirmed live: a single global timestamp, not a per-instrument list.
    private const string KpisUpdatedFixture = """
    { "kpisCalcUpdated": "2026-09-12T06:14:52.413" }
    """;

    // Confirmed live: global insIds start well above the Nordic range (real example: 10054).
    private const string GlobalInstrumentsFixture = """
    { "instruments": [
      { "insId": 10054, "name": "FG Nexus Inc", "ticker": "FGNX", "isin": "US00000FGNX0", "marketId": 33, "countryId": 5, "sectorId": 9, "branchId": 9 },
      { "insId": 10055, "name": "Some Other Global Co", "ticker": "SOGC", "isin": "US00000SOGC0", "marketId": 33, "countryId": 5, "sectorId": 9, "branchId": 9 }
    ] }
    """;

    // Matches Börsdata's CompaniesDescriptionArrayRespV1/CompanyDescriptionV1 schema (confirmed
    // against the OpenAPI spec at apidoc.borsdata.se/swagger/v1/swagger.json) — keyed by "list",
    // each entry has "insId" plus languageCode/text, with an optional per-entry "error" field.
    private const string InstrumentDescriptionsFixture = """
    { "list": [
      { "insId": 1, "languageCode": "en", "text": "Volvo is a Swedish manufacturer." },
      { "insId": 999, "error": "Instrument not found" }
    ] }
    """;

    private sealed class StubHandler : DelegatingHandler
    {
        private readonly TimeSpan delay;
        private int callCount;
        public int CallCount => callCount;
        public Uri? LastRequestUri { get; private set; }

        public StubHandler(TimeSpan delay = default) => this.delay = delay;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref callCount);
            LastRequestUri = request.RequestUri;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            var body = path.Contains("kpis/metadata") ? KpiMetadataFixture
                : path.Contains("kpis/updated") ? KpisUpdatedFixture
                : path.Contains("stocksplits") ? StockSplitsFixture
                : path.Contains("reports/metadata") ? ReportMetadataFixture
                : path.Contains("translationmetadata") ? TranslationMetadataFixture
                : path.Contains("instruments/updated") ? InstrumentsUpdatedFixture
                : path.Contains("instruments/description") ? InstrumentDescriptionsFixture
                : path.Contains("instruments/global") ? GlobalInstrumentsFixture
                : InstrumentsFixture;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private static BorsdataApiClient CreateClient(out StubHandler stub, TimeSpan delay = default)
    {
        stub = new StubHandler(delay);
        var httpClient = new HttpClient(stub) { BaseAddress = new Uri("https://apiservice.borsdata.se/v1/") };
        return new BorsdataApiClient(httpClient, new MemoryCache(new MemoryCacheOptions()));
    }

    [Fact]
    public async Task ListInstruments_NoFilters_ReturnsEverything()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await ReferenceDataTools.ListInstruments(client))!;

        Assert.Equal(6, result["totalMatched"]!.GetValue<int>());
        Assert.Equal(6, result["returned"]!.GetValue<int>());
        Assert.Equal(6, result["instruments"]!.AsArray().Count);
    }

    [Fact]
    public async Task ListInstruments_SearchMatchesNameCaseInsensitively()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await ReferenceDataTools.ListInstruments(client, search: "volvo"))!;

        Assert.Equal(2, result["totalMatched"]!.GetValue<int>());
        var names = result["instruments"]!.AsArray().Select(i => i!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("Volvo A", names);
        Assert.Contains("Volvo B", names);
    }

    [Fact]
    public async Task ListInstruments_SearchMatchesTicker()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await ReferenceDataTools.ListInstruments(client, search: "ERIC"))!;

        Assert.Equal(1, result["totalMatched"]!.GetValue<int>());
        Assert.Equal("Ericsson B", result["instruments"]![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task ListInstruments_SearchMatchesIsin()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await ReferenceDataTools.ListInstruments(client, search: "FI4000297767"))!;

        Assert.Equal(1, result["totalMatched"]!.GetValue<int>());
        Assert.Equal("Nordea Bank", result["instruments"]![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task ListInstruments_MarketIdIsolatesExpectedSubset()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await ReferenceDataTools.ListInstruments(client, marketId: 2))!;

        Assert.Equal(1, result["totalMatched"]!.GetValue<int>());
        Assert.Equal("Nordea Bank", result["instruments"]![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task ListInstruments_CombinesFiltersWithAnd()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await ReferenceDataTools.ListInstruments(client, marketId: 1, sectorId: 1))!;

        Assert.Equal(3, result["totalMatched"]!.GetValue<int>());
        var names = result["instruments"]!.AsArray().Select(i => i!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("Volvo A", names);
        Assert.Contains("Volvo B", names);
        Assert.Contains("Atlas Copco A", names);
    }

    [Fact]
    public async Task ListInstruments_MaxCountCapsReturned_ButNotTotalMatched()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await ReferenceDataTools.ListInstruments(client, marketId: 1, sectorId: 1, maxCount: 2))!;

        Assert.Equal(3, result["totalMatched"]!.GetValue<int>());
        Assert.Equal(2, result["returned"]!.GetValue<int>());
        Assert.Equal(2, result["instruments"]!.AsArray().Count);
    }

    [Fact]
    public async Task ListInstruments_OmittingMaxCount_ReturnsAllMatches_EvenWhenManyMatch()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await ReferenceDataTools.ListInstruments(client))!;

        Assert.Equal(6, result["returned"]!.GetValue<int>());
    }

    [Fact]
    public async Task ListInstruments_Caches_AcrossDifferentFilters_AndAlwaysSeesFullList()
    {
        var client = CreateClient(out var stub);

        var filtered = JsonNode.Parse(await ReferenceDataTools.ListInstruments(client, search: "volvo"))!;
        Assert.Equal(2, filtered["totalMatched"]!.GetValue<int>());

        var unfiltered = JsonNode.Parse(await ReferenceDataTools.ListInstruments(client))!;

        Assert.Equal(6, unfiltered["totalMatched"]!.GetValue<int>());
        Assert.Equal(1, stub.CallCount);
    }

    [Fact]
    public async Task ListInstruments_ConcurrentColdCalls_OnlyFetchOnce()
    {
        // Regression test: IMemoryCache.GetOrCreateAsync does not serialize concurrent misses on the
        // same key, so two tool calls racing on a cold cache would otherwise both hit the live API.
        // Deliberately uses two separate BorsdataApiClient instances sharing one HttpClient/IMemoryCache
        // (not CreateClient's single shared instance) because Program.cs's AddHttpClient<BorsdataApiClient>()
        // registers the class as transient — a real MCP client's two concurrent tool calls get two
        // different instances in production, so a per-instance lock would not have caught this.
        var stub = new StubHandler(delay: TimeSpan.FromMilliseconds(100));
        var httpClient = new HttpClient(stub) { BaseAddress = new Uri("https://apiservice.borsdata.se/v1/") };
        var cache = new MemoryCache(new MemoryCacheOptions());
        var clientForCall1 = new BorsdataApiClient(httpClient, cache);
        var clientForCall2 = new BorsdataApiClient(httpClient, cache);

        var first = ReferenceDataTools.ListInstruments(clientForCall1, search: "volvo");
        var second = ReferenceDataTools.ListInstruments(clientForCall2, search: "hacksaw");
        await Task.WhenAll(first, second);

        Assert.Equal(1, stub.CallCount);
    }

    [Fact]
    public async Task ListMarkets_RoundTripsFixtureUnchanged_NoFilteringApplied()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await ReferenceDataTools.ListMarkets(client, CancellationToken.None))!;

        Assert.Equal(6, result["instruments"]!.AsArray().Count);
    }

    [Fact]
    public async Task ListKpiMetadata_RoundTripsFixtureUnchanged()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await ReferenceDataTools.ListKpiMetadata(client, CancellationToken.None))!;

        var kpis = result["kpiHistoryMetadatas"]!.AsArray();
        Assert.Equal(2, kpis.Count);
        Assert.Equal("P/E", kpis.Single(k => k!["kpiId"]!.GetValue<int>() == 2)!["nameEn"]!.GetValue<string>());
    }

    [Fact]
    public async Task ListKpiMetadata_IsCached_DoesNotRefetchOnSecondCall()
    {
        var client = CreateClient(out var stub);

        await ReferenceDataTools.ListKpiMetadata(client, CancellationToken.None);
        await ReferenceDataTools.ListKpiMetadata(client, CancellationToken.None);

        Assert.Equal(1, stub.CallCount);
    }

    [Fact]
    public async Task GetStockSplits_EnrichesKnownInstrumentWithTickerAndName()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await ReferenceDataTools.GetStockSplits(client, CancellationToken.None))!;

        var splits = result["splits"]!.AsArray();
        Assert.Equal(2, splits.Count);
        var known = splits.Single(s => s!["instrumentId"]!.GetValue<int>() == 1)!;
        Assert.Equal("Volvo B", known["name"]!.GetValue<string>());
        Assert.Equal("VOLV B", known["ticker"]!.GetValue<string>());
        Assert.Equal("1:100", known["ratio"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetStockSplits_UnknownInstrumentId_OmitsTickerAndNameButKeepsData()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await ReferenceDataTools.GetStockSplits(client, CancellationToken.None))!;

        var unknown = result["splits"]!.AsArray().Single(s => s!["instrumentId"]!.GetValue<int>() == 999)!;
        Assert.Null(unknown["name"]);
        Assert.Equal("4:1", unknown["ratio"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetStockSplits_IsCached_DoesNotRefetchOnSecondCall()
    {
        var client = CreateClient(out var stub);

        await ReferenceDataTools.GetStockSplits(client, CancellationToken.None);
        await ReferenceDataTools.GetStockSplits(client, CancellationToken.None);

        // 1 stocksplits fetch (cached) + 1 instruments fetch (cached) = 2 total across both calls.
        Assert.Equal(2, stub.CallCount);
    }

    [Fact]
    public async Task ListReportMetadata_RoundTripsFixtureUnchanged()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await ReferenceDataTools.ListReportMetadata(client, CancellationToken.None))!;

        var fields = result["reportMetadatas"]!.AsArray();
        Assert.Equal(2, fields.Count);
        var cash = fields.Single(f => f!["reportPropery"]!.GetValue<string>() == "cash_And_Equivalents")!;
        Assert.Equal("Cash and equivalents", cash["nameEn"]!.GetValue<string>());
        Assert.Equal("MCURR", cash["format"]!.GetValue<string>());
    }

    [Fact]
    public async Task ListReportMetadata_IsCached_DoesNotRefetchOnSecondCall()
    {
        var client = CreateClient(out var stub);

        await ReferenceDataTools.ListReportMetadata(client, CancellationToken.None);
        await ReferenceDataTools.ListReportMetadata(client, CancellationToken.None);

        Assert.Equal(1, stub.CallCount);
    }

    [Fact]
    public async Task ListTranslationMetadata_RoundTripsFixtureUnchanged()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await ReferenceDataTools.ListTranslationMetadata(client, CancellationToken.None))!;

        var entries = result["translationMetadatas"]!.AsArray();
        Assert.Equal(2, entries.Count);
        var financials = entries.Single(e => e!["translationKey"]!.GetValue<string>() == "L_SECTOR_1")!;
        Assert.Equal("Financials", financials["nameEn"]!.GetValue<string>());
    }

    [Fact]
    public async Task ListTranslationMetadata_IsCached_DoesNotRefetchOnSecondCall()
    {
        var client = CreateClient(out var stub);

        await ReferenceDataTools.ListTranslationMetadata(client, CancellationToken.None);
        await ReferenceDataTools.ListTranslationMetadata(client, CancellationToken.None);

        Assert.Equal(1, stub.CallCount);
    }

    [Fact]
    public async Task GetInstrumentDescriptions_BuildsCorrectPathAndQuery_AndReturnsRawResponse()
    {
        var client = CreateClient(out var stub);

        var result = JsonNode.Parse(await ReferenceDataTools.GetInstrumentDescriptions(
            client, instrumentIds: "1,999", CancellationToken.None))!;

        Assert.Equal("/v1/instruments/description", stub.LastRequestUri!.AbsolutePath);
        Assert.Equal("instList=1%2C999", stub.LastRequestUri.Query.TrimStart('?'));
        var list = result["list"]!.AsArray();
        Assert.Equal(2, list.Count);
        Assert.Equal("Volvo is a Swedish manufacturer.", list[0]!["text"]!.GetValue<string>());
        Assert.Equal("Instrument not found", list[1]!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetInstrumentsUpdated_NoFilters_SortsMostRecentlyUpdatedFirst_EnrichesKnownInstruments()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await ReferenceDataTools.GetInstrumentsUpdated(client))!;

        Assert.Equal(3, result["totalMatched"]!.GetValue<int>());
        var insIds = result["values"]!.AsArray().Select(v => v!["insId"]!.GetValue<int>()).ToList();
        Assert.Equal([999, 2, 1], insIds);

        var volvo = result["values"]!.AsArray().Single(v => v!["insId"]!.GetValue<int>() == 1)!;
        Assert.Equal("Volvo B", volvo["name"]!.GetValue<string>());
        Assert.Equal("VOLV B", volvo["ticker"]!.GetValue<string>());

        var unknown = result["values"]!.AsArray().Single(v => v!["insId"]!.GetValue<int>() == 999)!;
        Assert.Null(unknown["name"]);
    }

    [Fact]
    public async Task GetInstrumentsUpdated_FiltersByInstrumentIds()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await ReferenceDataTools.GetInstrumentsUpdated(client, instrumentIds: "1,2"))!;

        Assert.Equal(2, result["totalMatched"]!.GetValue<int>());
    }

    [Fact]
    public async Task GetInstrumentsUpdated_MaxCountCapsReturned_ButNotTotalMatched()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await ReferenceDataTools.GetInstrumentsUpdated(client, maxCount: 1))!;

        Assert.Equal(3, result["totalMatched"]!.GetValue<int>());
        Assert.Equal(1, result["returned"]!.GetValue<int>());
        Assert.Equal(999, result["values"]![0]!["insId"]!.GetValue<int>());
    }

    [Fact]
    public async Task GetInstrumentsUpdated_NotCached_RefetchesOnSecondCall()
    {
        var client = CreateClient(out var stub);

        await ReferenceDataTools.GetInstrumentsUpdated(client);
        await ReferenceDataTools.GetInstrumentsUpdated(client);

        // 2 instruments/updated fetches (not cached, freshness data) + 1 instruments fetch (cached).
        Assert.Equal(3, stub.CallCount);
    }

    [Fact]
    public async Task GetKpisUpdated_ReturnsRawTimestamp()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await ReferenceDataTools.GetKpisUpdated(client, CancellationToken.None))!;

        Assert.Equal("2026-09-12T06:14:52.413", result["kpisCalcUpdated"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetKpisUpdated_NotCached_RefetchesOnSecondCall()
    {
        var client = CreateClient(out var stub);

        await ReferenceDataTools.GetKpisUpdated(client, CancellationToken.None);
        await ReferenceDataTools.GetKpisUpdated(client, CancellationToken.None);

        Assert.Equal(2, stub.CallCount);
    }

    [Fact]
    public async Task ListInstruments_IncludeGlobalFalse_NoIsGlobalField_AndNeverFetchesGlobalList()
    {
        var client = CreateClient(out var stub);

        var result = JsonNode.Parse(await ReferenceDataTools.ListInstruments(client))!;

        Assert.Equal(6, result["totalMatched"]!.GetValue<int>());
        foreach (var instrument in result["instruments"]!.AsArray())
            Assert.Null(instrument!["isGlobal"]);

        // Only the Nordic instruments endpoint should have been hit — proves the includeGlobal ?
        // ... : null short-circuit actually skips the second fetch, not just skips merging after
        // fetching both.
        Assert.Equal(1, stub.CallCount);
    }

    [Fact]
    public async Task ListInstruments_IncludeGlobalTrue_MergesBothUniverses_AndTagsCorrectly()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await ReferenceDataTools.ListInstruments(client, includeGlobal: true))!;

        Assert.Equal(8, result["totalMatched"]!.GetValue<int>());
        var volvo = result["instruments"]!.AsArray().Single(i => i!["insId"]!.GetValue<int>() == 1)!;
        Assert.False(volvo["isGlobal"]!.GetValue<bool>());
        var globalCo = result["instruments"]!.AsArray().Single(i => i!["insId"]!.GetValue<int>() == 10054)!;
        Assert.True(globalCo["isGlobal"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ListInstruments_IncludeGlobalTrue_SearchMatchesAcrossBothUniverses()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await ReferenceDataTools.ListInstruments(client, search: "Nexus", includeGlobal: true))!;

        Assert.Equal(1, result["totalMatched"]!.GetValue<int>());
        Assert.Equal(10054, result["instruments"]![0]!["insId"]!.GetValue<int>());
    }

    [Fact]
    public async Task ListInstruments_IncludeGlobalTrue_CachesGlobalListSeparately()
    {
        var client = CreateClient(out var stub);

        await ReferenceDataTools.ListInstruments(client, includeGlobal: true);
        await ReferenceDataTools.ListInstruments(client, includeGlobal: true);

        // 1 Nordic fetch + 1 global fetch, both cached — a second includeGlobal:true call adds 0 more.
        Assert.Equal(2, stub.CallCount);
    }
}
