namespace BorsdataMcp.Tests;

public class InstrumentSortingTests
{
    private sealed record Item(long Id, string? Ticker, string? Name);

    [Theory]
    [InlineData("ticker", "asc", new long[] { 1, 2, 3, 4 })]
    [InlineData("ticker", "desc", new long[] { 3, 1, 2, 4 })]
    [InlineData("name", "asc", new long[] { 1, 2, 3, 4 })]
    [InlineData("name", "desc", new long[] { 3, 1, 2, 4 })]
    public void Sort_IsCaseInsensitiveDeterministicAndPlacesMissingValuesLast(
        string sortBy,
        string direction,
        long[] expected)
    {
        Item[] values =
        [
            new(3, "BBB", "Beta"),
            new(2, "aaa", "alpha"),
            new(4, null, null),
            new(1, "AAA", "Alpha")
        ];

        var result = InstrumentSorting.Sort(
            values, sortBy, direction, item => item.Ticker, item => item.Name, item => item.Id);

        Assert.Equal(expected, result.Select(item => item.Id));
    }
}
