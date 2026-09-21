using System.Text.Json.Nodes;
using BorsdataMcp.Tools;

namespace BorsdataMcp.Tests;

public class KpiScreenerCatalogTests
{
    [Fact]
    public void CatalogLoadsCompleteOfficialTableAndRecognizesExactCombinations()
    {
        var catalog = new KpiScreenerCatalog();

        Assert.Equal(2592, catalog.Options.Count);
        Assert.Equal(182, catalog.Options.Select(option => option.KpiId).Distinct().Count());
        Assert.True(catalog.Contains(2, "last", "latest"));
        Assert.True(catalog.Contains(97, "5year", "cagr"));
        Assert.False(catalog.Contains(97, "year", "cagr5y"));
    }

    [Fact]
    public void LookupByKpiIdReturnsEveryExactEarningsGrowthCombination()
    {
        var result = JsonNode.Parse(new KpiScreenerCatalog().Search(97, null, null))!;

        Assert.Equal(25, result["totalMatched"]!.GetValue<int>());
        Assert.Equal(25, result["returned"]!.GetValue<int>());
        Assert.Contains(result["options"]!.AsArray(), option =>
            option!["calcGroup"]!.GetValue<string>() == "5year" &&
            option["calc"]!.GetValue<string>() == "cagr");
    }

    [Fact]
    public void LookupToolSearchesEnglishNamesAndHonorsLimit()
    {
        var json = ScreeningTools.ListKpiScreenerOptions(
            new KpiScreenerCatalog(), query: "P/E", maxCount: 3);
        var result = JsonNode.Parse(json)!;

        Assert.True(result["totalMatched"]!.GetValue<int>() > 3);
        Assert.Equal(3, result["returned"]!.GetValue<int>());
        Assert.Equal(3, result["options"]!.AsArray().Count);
    }

    [Fact]
    public void LookupMatchesTermsRegardlessOfOrder()
    {
        var result = JsonNode.Parse(new KpiScreenerCatalog().Search(
            null, "earnings growth 5year CAGR", null))!;

        Assert.Equal(1, result["totalMatched"]!.GetValue<int>());
        var option = result["options"]![0]!;
        Assert.Equal(97, option["kpiId"]!.GetValue<int>());
        Assert.Equal("5year", option["calcGroup"]!.GetValue<string>());
        Assert.Equal("cagr", option["calc"]!.GetValue<string>());
    }

    [Fact]
    public void LookupMapsNetIncomeAliasToEarningsKpi()
    {
        var result = JsonNode.Parse(new KpiScreenerCatalog().Search(
            null, "net income 5year cagr", null))!;

        Assert.Equal(1, result["totalMatched"]!.GetValue<int>());
        Assert.Equal(56, result["options"]![0]!["kpiId"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(0, null, null)]
    [InlineData(2, null, 0)]
    [InlineData(2, null, 201)]
    public void LookupRejectsInvalidRequests(int? kpiId, string? query, int? maxCount)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new KpiScreenerCatalog().Search(kpiId, query, maxCount));

        Assert.StartsWith("INVALID_REQUEST:", error.Message);
    }
}
