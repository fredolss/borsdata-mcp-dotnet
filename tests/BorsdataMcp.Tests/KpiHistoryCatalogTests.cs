using System.Text.Json.Nodes;
using BorsdataMcp.Tools;

namespace BorsdataMcp.Tests;

public class KpiHistoryCatalogTests
{
    [Fact]
    public void CatalogLoadsCompleteOfficialTableAndRecognizesExactCombinations()
    {
        var catalog = new KpiHistoryCatalog();

        Assert.Equal(297, catalog.Options.Count);
        Assert.Equal(91, catalog.Options.Select(option => option.KpiId).Distinct().Count());
        catalog.Validate(6, "year", "mean");
        catalog.Validate(2, "r12", "high");
        Assert.Throws<InvalidOperationException>(() => catalog.Validate(6, "year", "latest"));
    }

    [Fact]
    public void LookupByKpiIdReturnsExactEarningsPerShareCombinations()
    {
        var result = JsonNode.Parse(new KpiHistoryCatalog().Search(6, null, null))!;

        Assert.Equal(3, result["totalMatched"]!.GetValue<int>());
        Assert.Equal(3, result["returned"]!.GetValue<int>());
        Assert.All(result["options"]!.AsArray(), option =>
            Assert.Equal("mean", option!["priceType"]!.GetValue<string>()));
    }

    [Fact]
    public void LookupToolSearchesNamesAndHonorsLimit()
    {
        var json = ScreeningTools.ListKpiHistoryOptions(
            new KpiHistoryCatalog(), query: "P/E", maxCount: 3);
        var result = JsonNode.Parse(json)!;

        Assert.True(result["totalMatched"]!.GetValue<int>() > 3);
        Assert.Equal(3, result["returned"]!.GetValue<int>());
    }

    [Fact]
    public void LookupMapsNetIncomeAliasToHistoryKpi()
    {
        var result = JsonNode.Parse(new KpiHistoryCatalog().Search(
            null, "net income year mean", null))!;

        Assert.Equal(1, result["totalMatched"]!.GetValue<int>());
        Assert.Equal(56, result["options"]![0]!["kpiId"]!.GetValue<int>());
        Assert.Equal("year", result["options"]![0]!["reportType"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(0, null, null)]
    [InlineData(2, null, 0)]
    [InlineData(2, null, 201)]
    public void LookupRejectsInvalidRequests(int? kpiId, string? query, int? maxCount)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new KpiHistoryCatalog().Search(kpiId, query, maxCount));

        Assert.StartsWith("INVALID_REQUEST:", error.Message);
    }
}
