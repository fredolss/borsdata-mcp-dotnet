using System.Text.RegularExpressions;

namespace BorsdataMcp;

internal static partial class CatalogSearch
{
    public static bool MatchesAllTerms(string query, params string[] fields)
    {
        var searchable = string.Join(' ', fields);
        var queryTerms = Terms(query);
        if (queryTerms.Length == 0)
            return false;

        var searchableTerms = Terms(searchable).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return queryTerms.All(searchableTerms.Contains);
    }

    public static (string RemainingQuery, int? KpiId) ApplyKnownAlias(string query)
    {
        // Börsdata names KPI 56 "Earnings" in the screener table and "Vinst(M)" in the
        // history table. Models and users commonly call the same metric "net income".
        if (!query.Contains("net income", StringComparison.OrdinalIgnoreCase))
            return (query, null);

        return (NetIncomePattern().Replace(query, " ").Trim(), 56);
    }

    private static string[] Terms(string text) =>
        TermPattern().Matches(text)
            .Select(match => match.Value)
            .ToArray();

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex TermPattern();

    [GeneratedRegex(@"\bnet\s+income\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NetIncomePattern();
}
