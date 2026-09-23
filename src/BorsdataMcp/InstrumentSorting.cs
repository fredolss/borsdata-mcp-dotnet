namespace BorsdataMcp;

internal static class InstrumentSorting
{
    public static List<T> Sort<T>(
        IEnumerable<T> source,
        string? sortBy,
        string? sortDirection,
        Func<T, string?> tickerSelector,
        Func<T, string?> nameSelector,
        Func<T, long> idSelector)
    {
        var field = sortBy ?? "ticker";
        var direction = sortDirection ?? "asc";
        if (field is not ("ticker" or "name"))
            throw InvalidRequest("sortBy must be 'ticker' or 'name'.");
        if (direction is not ("asc" or "desc"))
            throw InvalidRequest("sortDirection must be 'asc' or 'desc'.");

        var selector = field == "ticker" ? tickerSelector : nameSelector;
        var descending = direction == "desc";
        var result = source.ToList();
        result.Sort((left, right) => Compare(left, right, selector, idSelector, descending));
        return result;
    }

    private static int Compare<T>(
        T left,
        T right,
        Func<T, string?> valueSelector,
        Func<T, long> idSelector,
        bool descending)
    {
        var leftValue = valueSelector(left);
        var rightValue = valueSelector(right);
        var leftMissing = string.IsNullOrWhiteSpace(leftValue);
        var rightMissing = string.IsNullOrWhiteSpace(rightValue);

        if (leftMissing != rightMissing)
            return leftMissing ? 1 : -1;

        if (!leftMissing)
        {
            var comparison = StringComparer.OrdinalIgnoreCase.Compare(leftValue, rightValue);
            if (comparison != 0)
                return descending ? -comparison : comparison;
        }

        return idSelector(left).CompareTo(idSelector(right));
    }

    private static InvalidOperationException InvalidRequest(string detail) =>
        new($"INVALID_REQUEST: {detail}");
}
