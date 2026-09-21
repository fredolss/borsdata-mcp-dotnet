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

    private static string[] Terms(string text) =>
        TermPattern().Matches(text)
            .Select(match => match.Value)
            .ToArray();

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex TermPattern();
}
