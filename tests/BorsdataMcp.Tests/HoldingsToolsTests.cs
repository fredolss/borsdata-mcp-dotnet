using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BorsdataMcp.Tools;
using Microsoft.Extensions.Caching.Memory;

namespace BorsdataMcp.Tests;

public class HoldingsToolsTests
{
    private const string InstrumentsFixture = """
    { "instruments": [
      { "insId": 236, "name": "Volvo B", "ticker": "VOLV B", "isin": "SE0000115446", "marketId": 1, "countryId": 1, "sectorId": 1, "branchId": 1 },
      { "insId": 2585, "name": "Hacksaw", "ticker": "HACK", "isin": "SE0025138357", "marketId": 1, "countryId": 1, "sectorId": 8, "branchId": 55 }
    ] }
    """;

    // Börsdata's insider transactions are oldest-first with undocumented transactionType codes
    // (confirmed live: values like 0, 3, 19, 25 — not a simple 1=buy/2=sell scheme). "shares" sign
    // is the reliable signal: positive = acquisition, negative = disposal.
    private const string InsiderHoldingsFixture = """
    { "list": [
      { "insId": 236, "values": [
        { "misc": true, "ownerName": "A", "shares": 250, "price": 93.97, "amount": 23492.5, "currency": "SEK", "transactionType": 0, "transactionDate": "2016-11-10T00:00:00" },
        { "misc": false, "ownerName": "B", "shares": -751, "price": 95.31, "amount": -71577.81, "currency": "SEK", "transactionType": 25, "transactionDate": "2016-10-24T00:00:00" },
        { "misc": false, "ownerName": "C", "shares": 5220, "price": 142.14, "amount": 741970.8, "currency": "SEK", "transactionType": 19, "transactionDate": "2026-05-26T00:00:00" }
      ] }
    ] }
    """;

    private const string ShortHoldingsFixture = """
    { "list": [
      { "insId": 236, "shortsProc": -7.69, "shortsHolders": 4.0, "lastTransactionDate": "2026-09-02" },
      { "insId": 2585, "shortsProc": -0.5, "shortsHolders": 1.0, "lastTransactionDate": "2026-09-02" },
      { "insId": 999, "shortsProc": -15.2, "shortsHolders": 6.0, "lastTransactionDate": "2026-09-02" },
      { "insId": 3, "shortsProc": null, "shortsHolders": 0.0, "lastTransactionDate": null }
    ] }
    """;

    // Confirmed live: oldest-first, 140 entries for Volvo B, "date" field name (not
    // "transactionDate" like insider holdings), no server-side date filtering or maxCount effect.
    private const string BuybackHoldingsFixture = """
    { "list": [
      { "insId": 236, "values": [
        { "change": 30291594, "changeProc": 0.0, "price": 27.6925, "currency": "SEK", "shares": 30291594, "sharesProc": 0.0, "date": "2000-12-14T00:00:00" },
        { "change": 8636485, "changeProc": 0.0, "price": 190.07, "currency": "SEK", "shares": 8636485, "sharesProc": 0.0, "date": "2001-02-01T00:00:00" },
        { "change": 6509312, "changeProc": 0.0, "price": 187.66, "currency": "SEK", "shares": 15145797, "sharesProc": 0.0, "date": "2018-10-26T00:00:00" }
      ] },
      { "insId": 2585, "values": [] }
    ] }
    """;

    private sealed class RoutingStubHandler : DelegatingHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var path = request.RequestUri!.AbsolutePath;
            var body = path.Contains("holdings/insider") ? InsiderHoldingsFixture
                : path.Contains("holdings/shorts") ? ShortHoldingsFixture
                : path.Contains("holdings/buyback") ? BuybackHoldingsFixture
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

    [Fact]
    public async Task GetInsiderHoldings_NoFilters_SortsMostRecentFirst()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await HoldingsTools.GetInsiderHoldings(client, instrumentIds: "236"))!;

        var volvo = result["instruments"]![0]!;
        Assert.Equal(3, volvo["totalMatched"]!.GetValue<int>());
        var owners = volvo["transactions"]!.AsArray().Select(t => t!["ownerName"]!.GetValue<string>()).ToList();
        Assert.Equal(["C", "A", "B"], owners);
    }

    [Fact]
    public async Task GetInsiderHoldings_DirectionIncrease_FiltersToPositiveShares()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await HoldingsTools.GetInsiderHoldings(client, instrumentIds: "236", direction: "increase"))!;

        var volvo = result["instruments"]![0]!;
        Assert.Equal(2, volvo["totalMatched"]!.GetValue<int>());
        var owners = volvo["transactions"]!.AsArray().Select(t => t!["ownerName"]!.GetValue<string>()).ToList();
        Assert.DoesNotContain("B", owners);
    }

    [Fact]
    public async Task GetInsiderHoldings_DirectionDecrease_FiltersToNegativeShares()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await HoldingsTools.GetInsiderHoldings(client, instrumentIds: "236", direction: "decrease"))!;

        var volvo = result["instruments"]![0]!;
        Assert.Equal(1, volvo["totalMatched"]!.GetValue<int>());
        Assert.Equal("B", volvo["transactions"]![0]!["ownerName"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetInsiderHoldings_MinAmount_FiltersByAbsoluteAmount()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await HoldingsTools.GetInsiderHoldings(client, instrumentIds: "236", minAmount: 50000))!;

        var volvo = result["instruments"]![0]!;
        Assert.Equal(2, volvo["totalMatched"]!.GetValue<int>());
        var owners = volvo["transactions"]!.AsArray().Select(t => t!["ownerName"]!.GetValue<string>()).ToList();
        Assert.Contains("B", owners);
        Assert.Contains("C", owners);
    }

    [Fact]
    public async Task GetInsiderHoldings_FromDate_FiltersByTransactionDate()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await HoldingsTools.GetInsiderHoldings(client, instrumentIds: "236", fromDate: "2020-01-01"))!;

        var volvo = result["instruments"]![0]!;
        Assert.Equal(1, volvo["totalMatched"]!.GetValue<int>());
        Assert.Equal("C", volvo["transactions"]![0]!["ownerName"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetInsiderHoldings_MaxCountCapsReturned_ButNotTotalMatched()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await HoldingsTools.GetInsiderHoldings(client, instrumentIds: "236", maxCount: 1))!;

        var volvo = result["instruments"]![0]!;
        Assert.Equal(3, volvo["totalMatched"]!.GetValue<int>());
        Assert.Equal(1, volvo["returned"]!.GetValue<int>());
        // Most recent (2026-05-26, owner C) first.
        Assert.Equal("C", volvo["transactions"]![0]!["ownerName"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetBuybackHoldings_NoFilters_SortsMostRecentFirst()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await HoldingsTools.GetBuybackHoldings(client, instrumentIds: "236"))!;

        var volvo = result["instruments"]![0]!;
        Assert.Equal(3, volvo["totalMatched"]!.GetValue<int>());
        var dates = volvo["buybacks"]!.AsArray().Select(b => b!["date"]!.GetValue<string>()).ToList();
        Assert.Equal(["2018-10-26T00:00:00", "2001-02-01T00:00:00", "2000-12-14T00:00:00"], dates);
    }

    [Fact]
    public async Task GetBuybackHoldings_FromDate_FiltersByDate()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await HoldingsTools.GetBuybackHoldings(client, instrumentIds: "236", fromDate: "2010-01-01"))!;

        var volvo = result["instruments"]![0]!;
        Assert.Equal(1, volvo["totalMatched"]!.GetValue<int>());
        Assert.Equal("2018-10-26T00:00:00", volvo["buybacks"]![0]!["date"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetBuybackHoldings_MaxCountCapsReturned_ButNotTotalMatched()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await HoldingsTools.GetBuybackHoldings(client, instrumentIds: "236", maxCount: 1))!;

        var volvo = result["instruments"]![0]!;
        Assert.Equal(3, volvo["totalMatched"]!.GetValue<int>());
        Assert.Equal(1, volvo["returned"]!.GetValue<int>());
        Assert.Equal("2018-10-26T00:00:00", volvo["buybacks"]![0]!["date"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetBuybackHoldings_HandlesInstrumentWithNoBuybacks()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await HoldingsTools.GetBuybackHoldings(client, instrumentIds: "236,2585"))!;

        var hacksaw = result["instruments"]!.AsArray().Single(i => i!["insId"]!.GetValue<int>() == 2585)!;
        Assert.Equal(0, hacksaw["totalMatched"]!.GetValue<int>());
        Assert.Empty(hacksaw["buybacks"]!.AsArray());
    }

    [Fact]
    public async Task GetShortHoldings_NoFilters_SortsDescendingByMagnitude_NullsLast_EnrichesKnownInstruments()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await HoldingsTools.GetShortHoldings(client))!;

        Assert.Equal(4, result["totalMatched"]!.GetValue<int>());
        var insIds = result["values"]!.AsArray().Select(v => v!["insId"]!.GetValue<int>()).ToList();
        Assert.Equal([999, 236, 2585, 3], insIds);

        var volvo = result["values"]!.AsArray().Single(v => v!["insId"]!.GetValue<int>() == 236)!;
        Assert.Equal("Volvo B", volvo["name"]!.GetValue<string>());
        Assert.Equal("VOLV B", volvo["ticker"]!.GetValue<string>());

        var unknown = result["values"]!.AsArray().Single(v => v!["insId"]!.GetValue<int>() == 999)!;
        Assert.Null(unknown["name"]);
    }

    [Fact]
    public async Task GetShortHoldings_SortAscending_NullsStillLast()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await HoldingsTools.GetShortHoldings(client, sortAscending: true))!;

        var insIds = result["values"]!.AsArray().Select(v => v!["insId"]!.GetValue<int>()).ToList();
        Assert.Equal([2585, 236, 999, 3], insIds);
    }

    [Fact]
    public async Task GetShortHoldings_FiltersByInstrumentIds()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await HoldingsTools.GetShortHoldings(client, instrumentIds: "236,2585"))!;

        Assert.Equal(2, result["totalMatched"]!.GetValue<int>());
    }

    [Fact]
    public async Task GetShortHoldings_MinShortingPercent_UsesAbsoluteValue()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await HoldingsTools.GetShortHoldings(client, minShortingPercent: 5))!;

        Assert.Equal(2, result["totalMatched"]!.GetValue<int>());
        var insIds = result["values"]!.AsArray().Select(v => v!["insId"]!.GetValue<int>()).ToList();
        Assert.Equal([999, 236], insIds);
    }

    [Fact]
    public async Task GetShortHoldings_MaxCountCapsReturned_ButNotTotalMatched()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await HoldingsTools.GetShortHoldings(client, maxCount: 2))!;

        Assert.Equal(4, result["totalMatched"]!.GetValue<int>());
        Assert.Equal(2, result["returned"]!.GetValue<int>());
    }
}
