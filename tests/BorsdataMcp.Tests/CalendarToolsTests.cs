using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BorsdataMcp.Tools;
using Microsoft.Extensions.Caching.Memory;

namespace BorsdataMcp.Tests;

public class CalendarToolsTests
{
    // Confirmed live: Börsdata returns each instrument's full past+future history in one list,
    // with no server-side maxCount for this endpoint (unlike stockprices/kpi history).
    private const string ReportCalendarFixture = """
    { "list": [
      { "insId": 236, "values": [
        { "releaseDate": "2023-01-26T00:00:00", "reportType": "Q4" },
        { "releaseDate": "2023-04-20T00:00:00", "reportType": "Q1" },
        { "releaseDate": "2026-04-24T00:00:00", "reportType": "Q1" },
        { "releaseDate": "2026-07-17T00:00:00", "reportType": "Q2" }
      ] },
      { "insId": 2585, "values": [
        { "releaseDate": "2026-02-17T00:00:00", "reportType": "Q4" },
        { "releaseDate": "2026-04-28T00:00:00", "reportType": "Q1" }
      ] }
    ] }
    """;

    // Confirmed live: dividend calendar uses "excludingDate" (the ex-dividend date), not
    // "releaseDate" — a different field name from the report calendar despite the identical
    // { "list": [ { "insId", "values": [ ... ] } ] } envelope shape.
    private const string DividendCalendarFixture = """
    { "list": [
      { "insId": 236, "values": [
        { "excludingDate": "2025-04-03T00:00:00", "amountPaid": 10.5, "currencyShortName": "SEK", "distributionFrequency": null, "dividendType": 1 },
        { "excludingDate": "2026-04-09T00:00:00", "amountPaid": 8.5, "currencyShortName": "SEK", "distributionFrequency": null, "dividendType": 0 }
      ] },
      { "insId": 2585, "values": [
        { "excludingDate": "2026-05-04T00:00:00", "amountPaid": 4.31, "currencyShortName": "SEK", "distributionFrequency": null, "dividendType": 0 }
      ] }
    ] }
    """;

    private sealed class StubHandler : DelegatingHandler
    {
        public int CallCount { get; private set; }
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri;
            var body = request.RequestUri!.AbsolutePath.Contains("/dividend/") ? DividendCalendarFixture : ReportCalendarFixture;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    private static BorsdataApiClient CreateClient(out StubHandler stub)
    {
        stub = new StubHandler();
        var httpClient = new HttpClient(stub) { BaseAddress = new Uri("https://apiservice.borsdata.se/v1/") };
        return new BorsdataApiClient(httpClient, new MemoryCache(new MemoryCacheOptions()));
    }

    [Fact]
    public async Task GetReportCalendar_NoFilters_ReturnsFullHistoryPerInstrument()
    {
        var client = CreateClient(out var stub);

        var result = JsonNode.Parse(await CalendarTools.GetReportCalendar(client, instrumentIds: "236,2585"))!;

        Assert.Equal("instList=236%2C2585", stub.LastRequestUri!.Query.TrimStart('?'));
        var instruments = result["instruments"]!.AsArray();
        Assert.Equal(2, instruments.Count);

        var volvo = instruments.Single(i => i!["insId"]!.GetValue<int>() == 236)!;
        Assert.Equal(4, volvo["totalMatched"]!.GetValue<int>());
        Assert.Equal(4, volvo["returned"]!.GetValue<int>());
    }

    [Fact]
    public async Task GetReportCalendar_FromDate_FiltersToUpcomingOnly()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await CalendarTools.GetReportCalendar(client, instrumentIds: "236", fromDate: "2026-01-01"))!;

        var volvo = result["instruments"]![0]!;
        Assert.Equal(2, volvo["totalMatched"]!.GetValue<int>());
        var dates = volvo["reports"]!.AsArray().Select(r => r!["releaseDate"]!.GetValue<string>()).ToList();
        Assert.All(dates, d => Assert.StartsWith("2026", d));
    }

    [Fact]
    public async Task GetReportCalendar_FromAndToDate_FiltersToRange()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await CalendarTools.GetReportCalendar(
            client, instrumentIds: "236", fromDate: "2023-01-01", toDate: "2023-12-31"))!;

        var volvo = result["instruments"]![0]!;
        Assert.Equal(2, volvo["totalMatched"]!.GetValue<int>());
    }

    [Fact]
    public async Task GetReportCalendar_MaxCountCapsReturned_ButNotTotalMatched_TakingEarliestMatches()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await CalendarTools.GetReportCalendar(
            client, instrumentIds: "236", fromDate: "2026-01-01", maxCount: 1))!;

        var volvo = result["instruments"]![0]!;
        Assert.Equal(2, volvo["totalMatched"]!.GetValue<int>());
        Assert.Equal(1, volvo["returned"]!.GetValue<int>());
        Assert.Equal("2026-04-24T00:00:00", volvo["reports"]![0]!["releaseDate"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetReportCalendar_HandlesMultipleInstrumentsIndependently()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await CalendarTools.GetReportCalendar(client, instrumentIds: "236,2585"))!;

        var hacksaw = result["instruments"]!.AsArray().Single(i => i!["insId"]!.GetValue<int>() == 2585)!;
        Assert.Equal(2, hacksaw["totalMatched"]!.GetValue<int>());
    }

    [Fact]
    public async Task GetDividendCalendar_NoFilters_ReturnsFullHistoryPerInstrument()
    {
        var client = CreateClient(out var stub);

        var result = JsonNode.Parse(await CalendarTools.GetDividendCalendar(client, instrumentIds: "236,2585"))!;

        Assert.Equal("instList=236%2C2585", stub.LastRequestUri!.Query.TrimStart('?'));
        var volvo = result["instruments"]!.AsArray().Single(i => i!["insId"]!.GetValue<int>() == 236)!;
        Assert.Equal(2, volvo["totalMatched"]!.GetValue<int>());
        Assert.Equal(10.5, volvo["dividends"]![0]!["amountPaid"]!.GetValue<double>());
    }

    [Fact]
    public async Task GetDividendCalendar_FiltersByExcludingDate_NotReleaseDate()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await CalendarTools.GetDividendCalendar(
            client, instrumentIds: "236", fromDate: "2026-01-01"))!;

        var volvo = result["instruments"]![0]!;
        Assert.Equal(1, volvo["totalMatched"]!.GetValue<int>());
        Assert.Equal("2026-04-09T00:00:00", volvo["dividends"]![0]!["excludingDate"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetDividendCalendar_MaxCountCapsReturned_ButNotTotalMatched()
    {
        var client = CreateClient(out _);

        var result = JsonNode.Parse(await CalendarTools.GetDividendCalendar(
            client, instrumentIds: "236", maxCount: 1))!;

        var volvo = result["instruments"]![0]!;
        Assert.Equal(2, volvo["totalMatched"]!.GetValue<int>());
        Assert.Equal(1, volvo["returned"]!.GetValue<int>());
    }
}
