using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace BorsdataMcp.IntegrationTests;

/// <summary>
/// Drives the real BorsdataMcp server (spawned as a subprocess by <see cref="McpServerFixture"/>)
/// through a real MCP client over stdio, verifying protocol-level correctness against a local fake
/// Börsdata backend. No real Börsdata API key or network access is used.
/// </summary>
public sealed class McpProtocolTests(McpServerFixture fixture) : IClassFixture<McpServerFixture>
{
    private static CancellationToken Timeout(int seconds = 15) => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    // 1. The server starts and the MCP initialize handshake completes. A passing fixture already
    // proves this (McpClient.CreateAsync performs the handshake internally); this test asserts on
    // the resulting server identity so the check is explicit rather than merely implicit.
    [Fact]
    public void Initialize_Completes_WithServerInfo()
    {
        Assert.NotNull(fixture.Client.ServerInfo);
        Assert.False(string.IsNullOrWhiteSpace(fixture.Client.ServerInfo.Name));
    }

    // 2. The client can call tools/list.
    [Fact]
    public async Task ListTools_ReturnsNonEmptyList()
    {
        var tools = await fixture.Client.ListToolsAsync(cancellationToken: Timeout());

        Assert.NotEmpty(tools);
    }

    // 3. Expected tools exist with correct names and valid input schemas.
    [Fact]
    public async Task ListTools_IncludesExpectedToolsWithValidSchemas()
    {
        var tools = await fixture.Client.ListToolsAsync(cancellationToken: Timeout());
        var byName = tools.ToDictionary(t => t.Name);

        var searchInstruments = Assert.Contains("search_instruments", byName);
        Assert.Equal(JsonValueKind.Object, searchInstruments.JsonSchema.ValueKind);
        var searchProperties = searchInstruments.JsonSchema.GetProperty("properties");
        Assert.True(searchProperties.TryGetProperty("search", out _));
        Assert.True(searchProperties.TryGetProperty("marketId", out _));
        Assert.True(searchProperties.TryGetProperty("cursor", out _));
        // Every search_instruments parameter is optional (all nullable with C# defaults).
        if (searchInstruments.JsonSchema.TryGetProperty("required", out var searchRequired))
        {
            Assert.Empty(searchRequired.EnumerateArray());
        }

        var listMarkets = Assert.Contains("list_markets", byName);
        Assert.Equal(JsonValueKind.Object, listMarkets.JsonSchema.ValueKind);

        var kpiHistoryArray = Assert.Contains("get_kpi_history_array", byName);
        Assert.True(kpiHistoryArray.JsonSchema.TryGetProperty("required", out var kpiHistoryRequired));
        var requiredNames = kpiHistoryRequired.EnumerateArray().Select(e => e.GetString()).ToHashSet();
        Assert.Contains("kpiId", requiredNames);
        Assert.Contains("reportType", requiredNames);
        Assert.Contains("priceType", requiredNames);
        Assert.Contains("instrumentIds", requiredNames);
    }

    // 4. The client can complete a representative tool call and get a valid response.
    [Fact]
    public async Task CallTool_ListMarkets_ReturnsFixtureData()
    {
        var result = await fixture.Client.CallToolAsync("list_markets", arguments: null, cancellationToken: Timeout());

        Assert.False(result.IsError ?? false);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        var json = JsonNode.Parse(text)!;
        var markets = json["markets"]!.AsArray();
        Assert.Equal(2, markets.Count);
        Assert.Equal("Stockholm Large Cap", markets[0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task CallTool_SearchInstruments_ReturnsFixtureData()
    {
        var result = await fixture.Client.CallToolAsync(
            "search_instruments",
            arguments: new Dictionary<string, object?> { ["search"] = "Volvo" },
            cancellationToken: Timeout());

        Assert.False(result.IsError ?? false);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        var json = JsonNode.Parse(text)!;
        var instruments = json["instruments"]!.AsArray();
        Assert.Single(instruments);
        Assert.Equal("Volvo B", instruments[0]!["name"]!.GetValue<string>());
    }

    // 5. The server reports errors correctly for invalid input. get_kpi_history_array's own
    // catalog validation (KpiHistoryCatalog.Validate) throws a plain InvalidOperationException —
    // not an McpException — for a syntactically valid but unsupported kpiId/reportType/priceType
    // combination, which is exactly the case Program.cs's AddCallToolFilter exists to rewrap into
    // an McpException so its real message reaches the client. The MCP SDK's own
    // CreateToolCallErrorResult (confirmed by reading ModelContextProtocol.Core 2.2.0's source)
    // always prefixes "An error occurred invoking '<tool>'" — for a plain, non-McpException
    // exception that's the *entire* message (no detail, no colon); for an McpException it appends
    // ": " plus the exception's own Message. So the real assertion isn't the prefix's absence, but
    // that the specific validation detail actually made it into the message — proving the filter's
    // rewrap-to-McpException path was taken rather than the generic bare-prefix fallback. This
    // needs no fake-server fixture, since catalog validation runs before any HTTP call.
    [Fact]
    public async Task CallTool_InvalidKpiHistoryCombination_ReturnsRealErrorMessage()
    {
        var result = await fixture.Client.CallToolAsync(
            "get_kpi_history_array",
            arguments: new Dictionary<string, object?>
            {
                ["kpiId"] = 2,
                ["reportType"] = "bogus",
                ["priceType"] = "mean",
                ["instrumentIds"] = "3",
            },
            cancellationToken: Timeout());

        Assert.True(result.IsError);
        var message = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("Invalid KPI history combination", message);
        Assert.DoesNotContain("invoking 'get_kpi_history_array'.", message); // the bare, detail-free fallback form
    }
}
