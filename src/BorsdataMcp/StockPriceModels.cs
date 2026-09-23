using System.ComponentModel;
using System.Text.Json.Serialization;

namespace BorsdataMcp;

[Description("A cursor-paginated page of daily stock-price records enriched with instrument metadata.")]
public sealed class StockPricePageResult
{
    [JsonPropertyName("totalMatched")]
    [Description("Total number of instruments matching the original request across all pages.")]
    public required int TotalMatched { get; init; }

    [JsonPropertyName("returned")]
    [Description("Number of instruments returned on this page.")]
    public required int Returned { get; init; }

    [JsonPropertyName("values")]
    [Description("Daily stock-price records on this page.")]
    public required IReadOnlyList<StockPriceResult> Values { get; init; }

    [JsonPropertyName("nextCursor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [Description("Opaque token for the next page. Omitted when no more results exist.")]
    public string? NextCursor { get; init; }
}

[Description("One instrument's daily OHLCV record. The compact field names match Börsdata's API.")]
public sealed class StockPriceResult
{
    [JsonPropertyName("i")]
    [Description("Instrument ID assigned by Börsdata.")]
    public required long InstrumentId { get; init; }

    [JsonPropertyName("ticker")]
    [Description("Instrument ticker from the selected Börsdata instrument registry; null when metadata is unavailable.")]
    public string? Ticker { get; init; }

    [JsonPropertyName("name")]
    [Description("Instrument name from the selected Börsdata instrument registry; null when metadata is unavailable.")]
    public string? Name { get; init; }

    [JsonPropertyName("d")]
    [Description("Trading date for this daily price record. For get_latest_stock_prices this is the latest available trading day, not necessarily today; for get_stock_prices_by_date it is the requested historical trading date.")]
    public string? Date { get; init; }

    [JsonPropertyName("h")]
    [Description("High: highest price during the trading day.")]
    public double? High { get; init; }

    [JsonPropertyName("l")]
    [Description("Low: lowest price during the trading day.")]
    public double? Low { get; init; }

    [JsonPropertyName("c")]
    [Description("Close: closing price for this daily trading record; it is not necessarily a real-time quote.")]
    public required double Close { get; init; }

    [JsonPropertyName("o")]
    [Description("Open: opening price during the trading day.")]
    public double? Open { get; init; }

    [JsonPropertyName("v")]
    [Description("Volume: total traded volume during the trading day.")]
    public long? Volume { get; init; }
}
