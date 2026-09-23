using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BorsdataMcp.Tests;

internal static class TestServices
{
    public static CursorPaginationService CreatePagination(
        IMemoryCache? cache = null,
        TimeProvider? timeProvider = null,
        ScreeningOptions? options = null) =>
        new(
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            Options.Create(options ?? new ScreeningOptions()),
            timeProvider ?? TimeProvider.System);
}
