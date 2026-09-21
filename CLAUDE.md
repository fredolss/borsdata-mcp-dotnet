# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

An MCP (Model Context Protocol) server, written in .NET, that exposes the
[Börsdata](https://borsdata.se) financial data API (instruments, markets,
stock prices, KPIs, financial reports) as MCP tools over stdio, using the
official `ModelContextProtocol` C# SDK.

## Commands

```bash
dotnet build                          # build the whole solution
dotnet test                           # run all tests
dotnet test --filter FullyQualifiedName~AuthKeyHandlerTests   # run a single test class
dotnet run --project src/BorsdataMcp  # run the MCP server over stdio
./run-dev.sh                          # same, but sources .env first (Linux/macOS)
```

The server talks stdio MCP protocol on stdout — running it directly in a
terminal just blocks waiting for JSON-RPC input on stdin (Ctrl+D / EOF to
exit cleanly). It's normally launched by an MCP client, not run interactively.

## Configuration

The Börsdata API key is read from `Borsdata:ApiKey` in
`src/BorsdataMcp/appsettings.json`, or from the `Borsdata__ApiKey`
environment variable (double underscore — standard ASP.NET Core config
binding). Never commit a real API key to `appsettings.json`.

For local dev/testing via `run-dev.sh`/`run-dev.cmd` (see below), the same
`.env` (gitignored, copied from `.env.example`) is sourced directly by the
script before launching `dotnet run`. Add new config variables to
`.env.example` (and `README.md`) when introducing them.

There is no Docker support — see the "no Docker" note under Architecture
for why, if you're tempted to reintroduce it for this project.

## Architecture

- `Program.cs` builds a generic host, registers `BorsdataApiClient` as a
  typed `HttpClient` (base address from `BorsdataOptions.BaseUrl`) with
  `AuthKeyHandler` as a `DelegatingHandler`, and calls
  `AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly()`.
  Tool classes need no manual registration — anything decorated with
  `[McpServerToolType]` in the assembly is picked up automatically.
  `Program.cs` also chains a `.WithRequestFilters(f => f.AddCallToolFilter(...))`
  onto that same builder — confirmed by reading the installed
  `ModelContextProtocol` 2.2.0 SDK source
  (`McpServerImpl.CreateToolCallErrorResult`) that **only exceptions of
  type `ModelContextProtocol.McpException` have their `.Message` forwarded
  to the calling MCP client**; every other exception thrown by any tool —
  a plain `ArgumentException`, `HttpRequestException`, anything — collapses
  to a bare `"An error occurred invoking 'X'."` with zero detail, no matter
  how informative the real exception message was. This filter catches any
  non-`McpException` and rethrows it as `new McpException(ex.Message, ex)`,
  so its message does get forwarded, for every tool regardless of
  registration path (confirmed: `AddCallToolFilter` composes inside
  `McpServerImpl`'s single shared dispatcher, the same one
  `WithToolsFromAssembly()`-registered tools all go through). This is what
  motivated `GetKpiListScreener`'s own validation to explicitly throw
  `McpException` rather than relying on this filter alone — see its own
  bullet below for why a hard-required C# parameter didn't work here even
  though this filter exists. Also fixes `GetKpiHistory`'s previously-silent
  Börsdata-400 `HttpRequestException` (see its own bullet) the same way,
  with no changes needed there — the filter is what makes the message
  reach the caller now, not a per-tool change.
- **Logging must go to stderr, never stdout.** The stdio transport uses
  stdout exclusively for JSON-RPC protocol messages; anything else written
  there corrupts the protocol stream. This is why `Program.cs` sets
  `LogToStandardErrorThreshold = LogLevel.Trace` on the console logger.
  Keep this in mind when adding any new console output.
- `AuthKeyHandler` appends `authKey=<key>` as a query parameter to every
  outgoing request (Börsdata's auth scheme), rather than using a header.
- `RateLimitHandler` enforces Börsdata's documented API limits (their
  README: max 100 calls/10s → 429 with `Retry-After`; a *soft*, non-enforced
  10,000 calls/24h guideline): a `System.Threading.RateLimiting`
  `FixedWindowRateLimiter` throttles proactively before each send, a 429
  triggers up to 3 attempts total honoring `Retry-After`, and a daily
  counter logs one `LogWarning` (never blocks) if the soft limit is
  crossed. The limits are hardcoded constants, not `BorsdataOptions`
  settings — they're facts about the Börsdata API, not something a user
  should need to tune.
  - **`RateLimitHandler` itself must be registered transient in
    `Program.cs`, not singleton — a real crash, found live in a real
    Claude Desktop session's MCP server log
    (`~/.config/Claude/logs/mcp-server-Börsdata.log`), not a hypothetical.**
    The rate limiter and daily counter used to live directly on
    `RateLimitHandler`, registered as a singleton so that state would
    survive `IHttpClientFactory`'s periodic handler-pipeline rebuilds
    (default every 2 minutes). That reasoning about the *state* was
    correct, but making the *handler itself* the singleton was wrong:
    `IHttpClientFactory` requires every `DelegatingHandler` in a rebuilt
    pipeline to have a null `InnerHandler` at build time, and reusing the
    same singleton instance across rebuilds throws `InvalidOperationException:
    The 'InnerHandler' property must be null. 'DelegatingHandler' instances
    provided to 'HttpMessageHandlerBuilder' must not be reused or cached.`
    the moment the first rebuild actually happens — after which **every**
    subsequent HTTP call fails the same way, permanently, until the
    process restarts. This went undetected through this entire project's
    development because every manual `run-dev.sh` test session ran for
    well under 2 minutes; it only surfaced once a real, long-lived Claude
    Desktop session (the exact scenario the 7-day reference-data cache TTL
    was designed to take advantage of) actually lived past the rebuild
    boundary. The fix: the stateful parts (the limiter, the daily counter)
    were extracted into `RateLimitState`, which *is* the singleton now
    (`Program.cs`); `RateLimitHandler` stays transient like `AuthKeyHandler`
    and takes `RateLimitState` via constructor injection — a fresh handler
    instance per pipeline rebuild (satisfying `IHttpClientFactory`'s
    contract) that still shares the same persistent rate-limiting state.
    Confirmed live by keeping a process alive past the 2-minute rebuild
    boundary and making a request on each side of it — both succeeded, no
    `InnerHandler` exception, matching `RateLimitHandlerTests`'
    `MultipleHandlerInstances_CanShareTheSameState_WithoutThrowing`
    regression test (which simulates the same rebuild with two separate
    handler instances sharing one `RateLimitState`).
- `BorsdataApiClient` is a thin wrapper: each method hits one Börsdata
  endpoint and returns the raw `JsonNode` response. Responses are **not**
  deserialized into strongly-typed DTOs — this is deliberate, since the
  exact Börsdata response schema per endpoint hasn't been verified against
  live data yet. Tool methods serialize the `JsonNode` straight back out as
  a JSON string. If/when DTOs are introduced, verify field names against
  real API responses first (Börsdata's JSON casing/shape is not something
  to assume from memory).
  - Confirmed against live data: the 5 reference-data endpoints
    (`instruments`, `markets`, `branches`, `sectors`, `countries`) each
    wrap their array in an envelope object keyed by the endpoint name —
    e.g. `{ "instruments": [ ... ] }`, **not** a bare JSON array. A first
    pass at `ListInstruments`' filtering assumed a bare array and silently
    matched zero instruments against the live API despite passing unit
    tests built on an (also wrong) bare-array fixture — a reminder that
    the "not verified against live data" caveat above is not theoretical.
  - The five reference-data methods (`GetInstrumentsAsync`,
    `GetMarketsAsync`, `GetBranchesAsync`, `GetSectorsAsync`,
    `GetCountriesAsync`) go through a shared `GetCachedAsync` helper backed
    by `IMemoryCache` (`ReferenceDataCacheTtl`, `AbsoluteExpirationRelativeToNow`
    — a hardcoded constant, like `RateLimitHandler`'s limits, since it's a
    fact about how often Börsdata's reference data actually changes, not
    something a user should tune). Set to **7 days**, not just 24h:
    confirmed live that a stdio MCP server process survives for a whole
    client session, not just one short back-and-forth with the model —
    this project's own MCP connection ran for hours across dozens of tool
    calls without restarting — so a longer TTL captures more real cache
    hits without meaningfully risking staleness for data that barely ever
    changes. `GetCachedAsync` takes an explicit `ttl` parameter (an
    overload defaults to `ReferenceDataCacheTtl` for the reference-data
    callers above) so the same caching machinery also backs the
    shorter-lived `MarketDataCacheTtl` tier described below. Unlike
    `RateLimitHandler`, `IMemoryCache` is registered
    as a plain DI singleton (`AddMemoryCache()` in `Program.cs`) with no
    special lifetime caveat needed — it isn't part of the
    `IHttpClientFactory` handler pipeline that gets periodically rebuilt.
    A cache-miss lock (`SemaphoreSlim`, guarding only the miss path so it
    never blocks cache hits) prevents concurrent tool calls from both
    fetching on a cold cache — `IMemoryCache.GetOrCreateAsync` alone does
    *not* serialize concurrent misses on the same key, confirmed by two
    parallel `ListInstruments` calls both hitting the live API before this
    was added. That lock **must be `static`**, not an instance field:
    `BorsdataApiClient` itself is registered transient (`AddHttpClient<TClient>()`
    gives every DI resolution a new `TClient` instance, even though the
    underlying `HttpMessageHandler` is pooled), so an instance-level lock
    silently fails to serialize anything across the separate instances two
    concurrent tool calls actually get — the same "transient class, but
    some state needs to persist across instances" shape `RateLimitHandler`
    has, just solved here with a `static` field instead of an injected
    singleton dependency (see `RateLimitState` above). Either approach
    works; reaching for "make the whole class a singleton" instead —
    `RateLimitHandler`'s original, broken approach — does not, once that
    class is also an `IHttpClientFactory` message handler.
    The cache always holds the **unfiltered** response for a given
    endpoint; per-call filtering (see `ListInstruments` below) happens
    downstream of the cache on every call, so it can never itself become
    stale/narrowed by a previous filtered call. The same cached `JsonNode`
    instance is handed to every caller — safe as long as nothing mutates
    it in place (`array.Add(...)`, `obj["x"] = ...`); `JsonNode.Parent`
    being single-parent-only means the runtime already throws if you try
    to insert a live child of the cached tree into another `JsonArray`
    without `.DeepClone()` first, which is exactly what `ListInstruments`'
    filtering does.
  - `ListInstruments` takes optional `search` (substring match against
    name/ticker/isin)/`marketId`/`countryId`/`sectorId`/`branchId`/
    `maxCount` filters and returns `{ totalMatched, returned, instruments }`
    rather than a bare array, so a caller can tell when more instruments
    matched than were returned. This exists because the endpoint covers
    several thousand instruments — calling it with zero filters previously
    dumped everything in one response, large enough that at least one MCP
    client failed to render it ("This response didn't load"). `maxCount`
    is nullable/uncapped by default (not a low hard default) so "give me
    everything" remains possible.
  - `ListInstruments` also takes an optional `includeGlobal` bool (default
    `false`, preserving the exact pre-existing Nordic-only output —
    including omitting the `isGlobal` field entirely, not just defaulting
    it to `false`, so existing callers see byte-identical responses). When
    `true`, results are merged with Börsdata's global (non-Nordic, Pro+)
    instrument universe (`GET instruments/global`, confirmed live: 16,129
    entries, 4.57MB — over 30x the Nordic list's ~1,700/145KB), each
    result tagged `isGlobal: true`/`false` so a caller can tell which
    universe an instrument came from. Cached the same way as the Nordic
    list (its own `ReferenceDataCacheTtl`-TTL cache key,
    `"instruments/global"`), so repeated `includeGlobal: true` calls cost
    one extra live fetch per TTL window, not one per call. `insId` is
    assumed unique across both universes
    (confirmed live: global insIds start at 10054+, well above the Nordic
    range) so no de-duplication happens. A bool rather than a Go-style
    `scope: "nordic"|"global"` string: this project prefers a typed
    parameter over a stringly-typed enum-like value where the underlying
    concept really is binary, and merging by default (rather than
    switching, like the three bulk tools below) is affordable specifically
    *because* this path is cached — unlike them.
  - `GetLatestStockPrices` (`GET instruments/stockprices/last`) and
    `GetStockPricesByDate` (`GET instruments/stockprices/date?date=...`)
    return every instrument's price in one call (~1,700 entries live,
    ~145KB — same oversized-response shape as `ListInstruments`/
    `GetKpiListScreener`/`GetShortHoldings`). Confirmed live: neither
    endpoint accepts `instList` server-side (passing one had no effect,
    every instrument still came back), so both tools filter by
    `instrumentIds` and enrich with `ticker`/`name` client-side via the
    shared `InstrumentLookup` helper — the same `BuildStockPricesResult`
    helper backs both tools, since they differ only in which endpoint they
    call. Also confirmed live: a weekend/holiday `date` returns an empty
    `stockPricesList` (HTTP 200) rather than an error, so
    `GetStockPricesByDate` doesn't need special-case handling for
    non-trading days. Both are cached via `MarketDataCacheTtl` (1 hour,
    much shorter than `ReferenceDataCacheTtl`) — confirmed live across
    several hours of this session that Börsdata's "latest" close price
    stays byte-identical until the next trading day (today 2026-09-12's
    "latest" price was still dated 2025-09-11 — a close, not a live
    quote), so short-TTL caching costs nothing in freshness while cutting
    out repeat multi-hundred-KB fetches within the same trading day.
    `GetStockPricesByDate` caches per requested date (the date is part of
    the cache key, via the query string already baked into the endpoint
    string passed to `GetCachedAsync`) rather than one shared entry — a
    deliberately simple uniform TTL rather than giving already-elapsed
    dates a longer TTL than "today", since a past date's data never
    changes but the added complexity of special-casing that wasn't judged
    worth it.
  - `GetKpiListScreener` (`MarketDataTools.cs`) is the same KPI screener
    calculation as `GetKpiScreener`/`GetKpiScreenerAsync`, but for every
    instrument at once (`GET instruments/kpis/{kpiId}/{calcGroup}/{calc}`,
    confirmed live to return ~14,000 entries, ~600KB) rather than one.
    Cached via `MarketDataCacheTtl` per `kpiId`/`calcGroup`/`calc`
    combination (the full endpoint path, including those three values, is
    the cache key) — confirmed live this session that a KPI value (e.g.
    P/E) stayed byte-identical across several hours, and the global
    `kpisCalcUpdated` timestamp (see `GetKpisUpdated` below) didn't move
    either, both pointing at a once-per-day recalculation rather than a
    continuously-changing value. Results are enriched with `ticker`/`name`
    by joining against the (cached)
    `GetInstrumentsAsync()` list; instruments not present in that list —
    confirmed live for some low/near-zero-value entries, likely delisted
    or non-Nordic instruments — come back with `value` but no
    `ticker`/`name`. Takes only `kpiId`/`calcGroup`/`calc` — no `sortDescending`,
    `maxCount`, `minValue`/`maxValue`, or `global` bool (the last split
    into a separate `GetGlobalKpiListScreener` tool). Every one of those
    was removed over the course of this session; see design 7/8 below for
    why each one specifically went, since they weren't all removed for
    the same reason.
  - **Scoping `GetKpiListScreener` went through eight different designs
    this session before landing on the current one** — worth reading in
    full before changing this tool again, since each earlier design looked
    reasonable and failed for a specific, confirmed-live reason:
    1. *Everything optional, `[Description]`-only guidance.* `instrumentIds`
       (comma-separated string), `marketId`/`countryId`/`sectorId`/
       `branchId` (resolved against the cached instrument list via
       `InstrumentLookup.FilterIdsByAttributes`, ANDed with `instrumentIds`
       if both given) were all optional, with the burden entirely on prose
       to get a calling model to (a) always pass a scope it already had and
       (b) prefer the cheap `marketId` filter over a long resolved
       `instrumentIds` list. Three progressively more explicit wording
       rounds (v1: "prefer instrumentIds to keep the response small" — this
       backfired, see the `minValue`/`maxValue` note below; v2: an explicit
       "STEP 1/STEP 2" instruction to always chain `ListInstruments` →
       `instrumentIds`; v3: "ONE call is enough" moved to the very first
       sentence) all failed to reliably stop a real Claude Desktop session
       from calling this tool without `instrumentIds` it had already
       resolved in the same conversation — confirmed live across multiple
       separate sessions, including one where the *very build* containing
       v3's wording and the new attribute-filter params was installed and
       active, and the model still ignored `marketId` entirely and called
       the tool bare. Conclusion: optional parameters are, in practice, a
       suggestion a calling model can silently ignore no matter how the
       prose is worded.
    2. *Split into two tools, `instrumentIds` required on one of them.*
       `GetKpiListScreenerAllInstruments` was added as a second tool with
       no `instrumentIds` parameter at all, for when there genuinely were
       none; `GetKpiListScreener`'s `instrumentIds` was made a hard C#
       required parameter (no default), forcing the generated JSON schema
       to mark it `required: true`, on the theory that a required parameter
       is a schema validation failure a client can't skip, unlike an
       optional one — and that tool *selection* is something calling models
       are empirically more reliable at than correctly populating one
       optional field inside a multi-purpose tool. Confirmed live this made
       things *worse*, not better: a session called the tool without
       `instrumentIds` for a genuinely unscoped query, and the missing-
       parameter check fired in the SDK's own argument marshaller
       (`Microsoft.Extensions.AI.AIFunctionFactory`) *before* this method's
       body ever ran, as a plain `System.ArgumentException`. Reading the
       installed `ModelContextProtocol` 2.2.0 SDK source
       (`McpServerImpl.CreateToolCallErrorResult`) explains why that
       mattered: **only exceptions of type `ModelContextProtocol.McpException`
       have their `.Message` forwarded to the calling client** — every
       other exception, that `ArgumentException` included, collapses to a
       bare `"An error occurred invoking 'get_kpi_list_screener'."` with
       zero diagnostic content, regardless of how informative the real
       exception message was. The calling model had nothing to self-correct
       on: it retried the identical failing call six times, never once
       called `GetKpiListScreenerAllInstruments` (confirmed absent from the
       entire session log), then abandoned bulk screening entirely and fell
       back to ~164 individual per-instrument calls. Switching
       `instrumentIds` back to `string? instrumentIds = null` with an
       explicit `string.IsNullOrWhiteSpace` check that threw `McpException`
       (whose `.Message` *does* get forwarded) fixed that specific failure
       — confirmed live afterward: a market-scoped query flowed correctly
       in one shot (`list_markets` → `list_instruments(marketId)` →
       `get_kpi_list_screener(instrumentIds)`, no wasted unscoped call).
    3. *Still two tools; the model reliably used `instrumentIds` over
       `marketId` even after design 2's fix landed.* With the missing-
       parameter case now fixed, live testing surfaced a different,
       narrower problem: a session that had already resolved a market's
       full `instrumentIds` list via `ListInstruments` kept passing that
       list to `GetKpiListScreener` instead of switching to
       `GetKpiListScreenerAllInstruments` with the cheaper `marketId`
       filter — even after both tools' `[Description]`s were rewritten
       multiple times to explicitly say "already having the ids in hand is
       not a reason to prefer this tool." This is the key realization that
       ended the two-tool approach: **`marketId`/`sectorId`/etc and
       `instrumentIds` are both applied purely client-side, in this same
       process, against the same cached full-universe fetch** — Börsdata's
       API itself is never filtered by either, so there is genuinely no
       server-load difference between the two approaches, only a request-
       payload-size difference for the calling model. Once that was
       understood, pushing a calling model to prefer one filter over the
       other via description text was solving a problem that barely
       mattered in the first place, and — consistent with every other
       finding in this saga — prose-level preferences between two
       functionally-equivalent options were not something wording could
       reliably enforce anyway.
    4. *One tool, `instrumentIds` as a real `int[]?` array (not a
       comma-separated string), plus `marketId`/`countryId`/`sectorId`/
       `branchId` still on this same tool, with the `McpException`-on-
       missing-scope pattern from design 2 generalized to "at least one of
       `instrumentIds`/`marketId`/`countryId`/`sectorId`/`branchId` must be
       set."* `GetKpiListScreenerAllInstruments` was deleted;
       `InstrumentLookup` gained a second `ParseIds(int[]?)` overload
       (trivial — null/empty check plus `.ToHashSet()`, no string parsing)
       alongside the original `ParseIds(string?)` still used by every
       *other* tool's comma-separated `instrumentIds`. This design kept the
       one thing from design 2 that was actually validated as correctness-
       motivated (never silently screen all ~14,000 instruments when *some*
       scope was intended) while dropping the thing that wasn't pulling its
       weight (two similarly-named tools whose only real difference was an
       unenforceable efficiency preference). Short-lived: it still carried
       `FilterIdsByAttributes` and the whole `marketId`/etc parameter
       surface forward from design 3, which design 5 below removed.
    5. *`marketId`/`countryId`/`sectorId`/`branchId` are
       gone from this tool entirely — `instrumentIds` (`int[]?`) is the
       only scope mechanism.* Prompted directly by the same insight that
       ended design 3 (client-side filtering means `marketId` and
       `instrumentIds` cost the same), taken one step further: if there's
       no cost reason to prefer one over the other, there's also no reason
       to *maintain two parallel filtering mechanisms* on this one tool —
       `ListInstruments` already resolves `marketId`/`countryId`/`sectorId`/
       `branchId` into insIds perfectly well, confirmed working live as a
       two-call chain (`ListInstruments(marketId)` → `GetKpiListScreener(
       instrumentIds)`), so duplicating that resolution logic here via
       `InstrumentLookup.FilterIdsByAttributes` was pure surface area for
       no benefit. `FilterIdsByAttributes` and its private `GetInt` helper
       were deleted from `InstrumentLookup.cs` (nothing else used them).
       `instrumentIds` stays `int[]? instrumentIds = null` rather than a
       hard-required parameter, for the same reason established in design
       2 — the SDK's own required-parameter `ArgumentException` isn't an
       `McpException`, so its message would never reach the client; the
       explicit `if (instrumentIds is null || instrumentIds.Length == 0)`
       check at the top of the method body throwing `McpException` is what
       actually gets a useful message through. That message now points the
       caller at `ListInstruments` rather than a sibling tool — there still
       isn't one, and now there's also no second filter mechanism on this
       tool to redirect to.
    6. *`instrumentIds` is gone too — `GetKpiListScreener`
       takes no instrument-level filter at all.* Prompted by actually
       reading Börsdata's own OpenAPI spec
       (apidoc.borsdata.se/swagger/v1/swagger.json) rather than assuming:
       the underlying endpoint this tool calls
       (`instruments/kpis/{kpiId}/{calcGroup}/{calc}`, Börsdata operationId
       `kpislistv1`, tag "Kpi Screener") has **no `instList` parameter at
       all** — confirmed directly from the spec's parameter list (just
       `kpiId`/`calcGroup`/`calc`/`authKey`). Every `instrumentIds` design
       this tool ever had (designs 1 through 5 above) was therefore always
       a *client-side-only* filter dressed up to look like real API
       scoping — Börsdata's server always returns literally every
       instrument regardless of what was passed, and design 2's whole
       "required scope prevents an accidental full-universe screen" premise
       was validating against a fiction, since the full universe was
       always fetched either way; only the *client-side* filtering step
       was ever being skipped. `GetKpiListScreener` is now a **direct,
       unmodified mirror of Börsdata's `kpislistv1` endpoint**: kpiId/
       calcGroup/calc (+ `minValue`/`maxValue`/`sortDescending`/`maxCount`/
       `global` as legitimate client-side post-processing — these don't
       misrepresent what the API does, they just shape the already-fetched
       full response) and nothing else. For specific instruments, the
       correct tool is `GetKpiScreener` (`kpisv1`, one insId per call) —
       "the client must make multiple calls instead of the one taking the
       instrument id," in the words that prompted this change — since
       that's the endpoint Börsdata actually built for per-instrument
       scoping. `InstrumentLookup.ParseIds(int[]?)` was deleted (nothing
       uses it anymore); `BuildKpiListScreenerResult` dropped its
       `instrumentIds`/`idFilter` parameters entirely. This same spec-
       reading pass also surfaced four genuinely missing endpoints Börsdata
       exposes that this project had no tool for at all — see
       `GetKpiHistoryArray`/`GetReportsCompound`/`GetReportsArray`/
       `GetInstrumentDescriptions` below — found specifically because they
       share the same `instruments/kpis/{kpiId}/...`-shaped URL prefix as
       `GetKpiListScreener` and were easy to conflate with it during this
       investigation despite being on a different Börsdata tag ("Kpi
       History") with different path params and, unlike `kpislistv1`,
       *do* accept `instList` server-side for real.
    7. *`minValue`/`maxValue` are gone too, and `global`
       became a separate tool (`GetGlobalKpiListScreener`) instead of a
       bool parameter.* Prompted by extending design 6's own principle
       ("does this parameter misrepresent server-side capability, and is
       there an alternative that actually has the capability?") past
       `instrumentIds` to the rest of the parameter list — but this time
       the answer differed per parameter, not uniformly "remove it," and
       that distinction is worth keeping straight:
       - `global` was never client-side at all — it always switched which
         real Börsdata endpoint got called (`kpislistv1` vs
         `kpislistglobalv1`), so nothing about it was ever a "fake"
         parameter. It moved to a separate tool anyway, to mirror
         Börsdata's own two-separate-endpoints structure directly (one
         tool per endpoint, consistent with how `GetKpiScreener`/
         `GetKpiHistory`/etc. already don't have a `global` switch either
         — `GetLatestStockPrices`/`GetStockPricesByDate` still do, and
         weren't touched here; this change was scoped to
         `GetKpiListScreener` specifically, not applied project-wide).
       - `sortDescending`/`maxCount` stayed. They're genuinely different
         from `instrumentIds`: they reorder/truncate an already-fully-
         fetched, fixed-size response — they never claimed to reduce what
         Börsdata computed or sent, so there was no false mental model to
         correct. Removing `maxCount` specifically would have resurrected
         a real, already-fixed bug: an unbounded ~14,000-entry dump that
         once broke at least one MCP client's rendering (see
         `ListInstruments` above).
       - `minValue`/`maxValue` were removed, on explicit instruction,
         despite this session initially pushing back that they're not
         analogous to `instrumentIds` either: Börsdata's API has no
         server-side value-threshold filter on *any* endpoint, so unlike
         `instrumentIds` (where `GetKpiScreener` covers the same need
         through the correct endpoint), removing these doesn't redirect to
         an alternative tool — it deletes value-threshold screening (e.g.
         "P/E under 15") from this project's capability entirely, with no
         replacement anywhere. Kept here as a deliberate, acknowledged
         trade-off rather than a discovery like the others in this list —
         if that capability is wanted back later, it has no natural home
         on any existing tool and would need a new one, since no Börsdata
         endpoint does this filtering server-side either.
       `BuildKpiListScreenerResult` lost its `minValue`/`maxValue`
       parameters and the filtering block that used them; `GetKpiListScreener`
       lost `global` (now calls `GetKpiListScreenerAsync`/`GetInstrumentsAsync`
       unconditionally); the new `GetGlobalKpiListScreener` tool calls
       `GetGlobalKpiListScreenerAsync`/`GetGlobalInstrumentsAsync`
       unconditionally, sharing the same (now-simplified)
       `BuildKpiListScreenerResult` helper.
    8. **Current design: `sortDescending`/`maxCount` are gone too — both
       `GetKpiListScreener` and `GetGlobalKpiListScreener` now take only
       `kpiId`/`calcGroup`/`calc`, nothing else.** Explicit instruction to
       finish what design 7 stopped short of: design 7's own reasoning for
       *keeping* these two ("they reorder/truncate an already-fetched
       response, they never claimed to reduce what Börsdata computed, so
       there's no false mental model to correct") was accurate but, on
       reflection when asked to remove them anyway, beside the real point
       being made across designs 6 through 8 as a whole — that this tool
       should be a maximally literal mirror of `kpislistv1`'s actual
       surface, full stop, not "mirror it, plus whatever convenience
       shaping seems harmless." `BuildKpiListScreenerResult` no longer
       sorts, filters, or takes a subset at all — it just walks Börsdata's
       `values` array in whatever order it arrived in and enriches each
       entry with `ticker`/`name`, nothing more. The response envelope
       also dropped `totalMatched`/`returned` (they only meant anything
       when a cap could make `returned < totalMatched`; with no cap they'd
       always be equal to each other and to `values.Length`, so keeping
       them would just be redundant surface, not a live tool preserved
       here — same instinct as removing the parameters). This does bring
       back, deliberately and knowingly, the exact "unbounded ~14,000-
       entry response" risk flagged in design 7 (see the `ListInstruments`
       precedent above) — no mitigation was added for it, since none was
       asked for; if this becomes a real problem again, that's the fix to
       revisit, not a reason to silently re-add `maxCount`.
  - **Four tools added after actually reading Börsdata's OpenAPI spec**
    (apidoc.borsdata.se/swagger/v1/swagger.json) surfaced endpoints this
    project had never implemented, despite looking similar to existing
    ones:
    - `GetKpiHistoryArray` (`GET instruments/kpis/{kpiId}/{reportType}/
      {priceType}/history`, operationId `histarraykpisv1`, tag "Kpi
      History") — KPI history for a *list* of instruments in one call, the
      bulk counterpart to `GetKpiHistory`. Easy to confuse with
      `GetKpiListScreener`'s endpoint at a glance (same
      `instruments/kpis/{kpiId}/...` prefix) but genuinely different:
      different tag, `reportType`/`priceType` path params (not
      `calcGroup`/`calc`), and — unlike `kpislistv1` — `instList` is a real,
      *required* server-side parameter here (confirmed in the spec), so
      `instrumentIds` is a required `string` (comma-separated, matching
      every other `instList`-backed tool's convention) with no client-side
      filtering needed at all. Response envelope is `kpisList`, keyed by
      `instrument` (not `insId`) with an optional per-entry `error` field
      — a plain pass-through, same as `GetKpiHistory`/`GetReports`.
    - `GetReportsCompound` (`GET instruments/{id}/reports`, operationId
      `reportscompoundv1`, tag "Reports") — all three report types (year/
      quarter/r12) for *one* instrument in a single call, instead of three
      separate `GetReports` calls. `maxYearCount`/`maxR12QCount`/`original`
      match Börsdata's own param names and defaults (10/10, max 20/40;
      `original` is Börsdata's 0/1 "return original currency" flag, exposed
      as a `bool?`). Plain pass-through, no envelope wrapper — `instrument`
      plus the three `reports*` arrays directly at the top level.
    - `GetReportsArray` (`GET instruments/reports`, operationId
      `reportsarrayv1`, tag "Reports") — the bulk, list-of-instruments
      version of `GetReportsCompound`; `instList` is required (confirmed in
      the spec). Envelope key is `reportList`, each entry keyed by
      `instrument` (not `insId`) with an optional per-entry `error` field —
      same shape family as `GetKpiHistoryArray`.
    - `GetInstrumentDescriptions` (`GET instruments/description`,
      operationId literally `"Instrument Description"` in Börsdata's spec
      — the one operationId that doesn't follow their usual lowercase-v1
      naming convention) — Swedish/English company description text for a
      list of instruments, capped at 50 per call (confirmed in the spec's
      own parameter description). Lives in `ReferenceDataTools.cs` rather
      than `MarketDataTools.cs` since it's identity/content data about
      instruments rather than financial data, though — like the other
      three tools here — it's deliberately *not* cached: it takes an
      arbitrary `instList` per call (unbounded distinct cache keys) the
      same way `GetReportsAsync`/`GetReportCalendarAsync`/etc. already
      don't cache for the same reason. Envelope key is `list`, each entry
      `{ insId, languageCode, text }` with an optional per-entry `error`
      field.
    All four are plain pass-throughs (no client-side enrichment with
    ticker/name) — deliberately consistent with the existing
    `GetReportCalendar`/`GetDividendCalendar`/`GetInsiderHoldings`/
    `GetBuybackHoldings` family (bulk endpoints scoped by a caller-supplied
    `instrumentIds` the caller already knows), not the
    `GetKpiListScreener`/`GetShortHoldings`/`GetLatestStockPrices` family
    (truly *unscoped*, whole-market dumps that need ticker/name to be
    useful at all).
  - `GetLatestStockPrices` and `GetStockPricesByDate` each additionally
    take an optional `global` bool (default `false`). Unlike
    `ListInstruments`' `includeGlobal`, this *switches* the data source
    rather than merging it — confirmed live global counterparts exist for
    both (`instruments/stockprices/global/last`,
    `instruments/stockprices/global/date`). These two are cached under the
    much shorter `MarketDataCacheTtl` (1h) rather than `ReferenceDataCacheTtl`
    (7d) — merging Nordic + global by default like `ListInstruments` does
    would still double live API traffic and payload size (global
    responses run 1.35MB–4.57MB) at least once per that shorter window on
    every call path regardless of whether a caller wanted global data —
    switching instead keeps `global:false` (the default) exactly as cheap
    as before this change, still paying only the Nordic side's cost.
    `global:true` also
    switches the ticker/name enrichment source to `GetGlobalInstrumentsAsync()`
    so results are enriched against the universe actually queried, not the
    Nordic list. Confirmed live for both, including the previously-
    unverified `GetStockPricesByDate` non-trading-day case on the global
    endpoint (a Sunday date returns an empty result, same as the Nordic
    endpoint). `GetKpiListScreener` originally had this same `global` bool
    switch (global counterpart confirmed at `instruments/global/kpis/
    {kpiId}/{calcGroup}/{calc}` — note `global` sits right after
    `instruments/` there, not appended like the other two, matching
    Börsdata's own URL layout) but design 7 in `GetKpiListScreener`'s own
    bullet above split it into a separate `GetGlobalKpiListScreener` tool
    instead, to mirror Börsdata's own two-separate-endpoints structure
    directly — a deliberate inconsistency with these other two tools'
    bool-switch approach, not an oversight; they weren't changed to match
    since nothing prompted revisiting them.
  - Every other per-instrument tool (`GetStockPrices`, `GetKpiScreener`,
    `GetKpiHistory`, `GetKpiSummary`, `GetReports`, `GetReportCalendar`,
    `GetDividendCalendar`, `GetInsiderHoldings`, `GetBuybackHoldings`)
    needed **no code changes** for global support — confirmed live that a
    global instrument's `insId` (e.g. 10054, "FG Nexus Inc"; also verified
    with Apple Inc, insId 10214) works transparently against every one of
    these, since `insId` is universal across Börsdata's Nordic and global
    universes. `GetInsiderHoldings`/`GetBuybackHoldings` return an empty
    result (HTTP 200, Börsdata's existing graceful "not applicable" shape,
    not an error) for most global instruments, since Börsdata doesn't
    track those disclosures outside Nordic markets — an existing data-
    availability fact surfaced through code paths that already handle
    empty results, not a gap. `GetShortHoldings` has no global variant at
    all and remains Nordic-only, matching Börsdata's own API surface.
  - `ListKpiMetadata` (`GET instruments/kpis/metadata`, cached like the
    other reference-data endpoints — confirmed live at 171 entries, ~17KB,
    small enough to return unfiltered like `ListMarkets`/`ListSectors`)
    lists every Börsdata KPI's `kpiId` plus Swedish/English name, so a
    caller can look up e.g. "P/E" → `kpiId: 2` for `GetKpiScreener`/
    `GetKpiHistory`/`GetKpiListScreener` instead of needing to already know
    the id. Its envelope key is `kpiHistoryMetadatas`, not `kpiMetadata` or
    a name matching the endpoint path like the other reference-data
    responses — confirmed live, don't assume the naming convention
    generalizes. That name is Börsdata's own field, returned as-is —
    renaming it in our tool layer was deliberately not done, since it would
    break the "return Börsdata's JSON as-is" pattern for a single cosmetic
    inconsistency.
  - `GetStockSplits` (`GET instruments/stocksplits`, cached like the other
    reference-data endpoints — confirmed live at 44 entries, ~4KB, small
    enough to return unfiltered like `ListMarkets`/`ListKpiMetadata`)
    returns Börsdata's stock-split history across all instruments.
    Confirmed live: this endpoint uses `instrumentId` as the field name,
    not the `insId` every other endpoint uses — another one-off naming
    inconsistency (like `kpiHistoryMetadatas` above) that's left as-is
    rather than normalized, for the same reason. Results are enriched
    with `ticker`/`name` via the shared `InstrumentLookup` helper (the
    same one `GetKpiListScreener`/`GetShortHoldings` use), keyed off that
    `instrumentId` field.
  - `ListReportMetadata` (`GET instruments/reports/metadata`, cached,
    confirmed live at 37 entries ~4KB) and `ListTranslationMetadata`
    (`GET translationmetadata` — confirmed live: no `instruments/` prefix,
    unlike every other reference-data endpoint; 138 entries, ~12KB) round
    out reference-data coverage alongside the other metadata tools
    (countries/markets/sectors/branches/kpis are already `ListCountries`/
    `ListMarkets`/`ListSectors`/`ListBranches`/`ListKpiMetadata`).
    Deliberately implemented as separate tools rather than one
    `get_metadata` tool with a `type` switch: Börsdata's API itself
    exposes these as distinct endpoints, so separate tools match the
    underlying API more directly. `ListReportMetadata` describes
    `GetReports`/`GetKpiSummary`'s own field names (e.g.
    `cash_And_Equivalents` → "Cash and equivalents",
    format `MCURR`); `ListTranslationMetadata` is a `translationKey` →
    Swedish/English name lookup used for various coded labels elsewhere
    in the API (e.g. sector names). Both returned unfiltered, like
    `ListMarkets`/`ListKpiMetadata` — small enough that no capping is
    needed.
  - `GetInstrumentsUpdated` (`GET instruments/updated`) and
    `GetKpisUpdated` (`GET instruments/kpis/updated`) round out this
    project's tool coverage. Neither is cached, unlike every other
    reference-data endpoint here: caching
    "when was this last updated" data would make it lie about freshness,
    which defeats the entire point of calling it.
    - `GetInstrumentsUpdated`: confirmed live this does **not** cover
      every instrument — only ~700 of Börsdata's 1,700+, and Volvo B
      (insId 236, an obviously active, well-known instrument) is absent
      entirely. It's closer to "instruments recently touched" than a
      complete freshness index — the tool description and parameter docs
      say so explicitly so a missing id isn't misread as "doesn't exist"
      or "very stale." Filtered/enriched/sorted (most-recently-updated
      first) the same way `GetShortHoldings` handles its own ~400-entry
      unfilterable-server-side list.
    - `GetKpisUpdated`: confirmed live this is a **single global
      timestamp** (`{"kpisCalcUpdated": "2026-09-12T06:14:52.413"}`), not
      a per-instrument list — the simplest response shape anywhere in
      this API. Don't assume it needs the same list-handling machinery as
      `GetInstrumentsUpdated` just because the names look parallel.
  - `GetKpiHistory` (`GET instruments/{insId}/kpis/{kpiId}/{reportType}/
    {priceType}/history?maxCount=N`) is a plain pass-through like
    `GetKpiScreener` — no client-side filtering needed, since (confirmed
    live) Börsdata's own `maxCount` query param already caps the
    per-instrument history it returns here (unlike `GetStockPrices`,
    where the same-named parameter means something entirely different —
    see its own bullet below). Not every `reportType`/
    `priceType` combination is valid for every KPI — confirmed live that
    `quarter`/`mean` returns an HTTP 400 for kpiId 2 (P/E) even though
    `year`/`mean` and `r12`/`mean` both work — so this isn't validated
    against a hardcoded whitelist; an invalid combination's error just
    propagates like it already does for other tools — and, since
    `Program.cs`'s `AddCallToolFilter` was added, that propagated
    `HttpRequestException`'s message now actually reaches the calling
    client instead of collapsing to a contentless generic error (see
    `Program.cs`'s own bullet above). That message itself used to be just
    .NET's bare `"Response status code does not indicate success: 400
    (Bad Request)."` even after the filter fix — confirmed live this was
    still not enough: a calling model that hit this exact `year`/`latest`-
    for-kpiId-2 case thrashed across many unrelated fallback strategies
    (individual per-instrument calls, shorter id lists, etc.) instead of
    the one fix that actually mattered, switching `calcGroup`, because
    knowing "it's a 400" without knowing *why* wasn't actionable.
    `BorsdataApiClient.GetAsync` now reads and includes Börsdata's own
    response body (bounded to 500 chars) in the thrown `HttpRequestException`'s
    message instead of discarding it via a bare `EnsureSuccessStatusCode()`
    — confirmed safe to include: the `path` string this method receives
    never contains the `authKey` query parameter, since `AuthKeyHandler`
    appends that later in the pipeline, after `GetAsync` has already built
    its exception message. `GetKpiScreener`/`GetKpiListScreener`'s
    `calcGroup` parameter `[Description]`s now also explicitly flag that
    `'last'` is confirmed to work broadly and `'year'` does not for kpiId
    2 — a concrete, checkable example rather than just "not every
    combination works."
  - `GetStockPrices` (`GET instruments/{insId}/stockprices?from=...&to=...
    &maxCount=...`) found via a real user-visible failure: a large-history
    instrument (`instrumentId: 352`, no `from`/`to`) returned thousands of
    days (~370KB) with an AI client unable to fit the tool result in
    context. First instinct — pass our `maxCount` through unchanged like
    every other endpoint — looked broken at first: an out-of-range value
    (200) returned the full unfiltered history instead of erroring or
    capping, which read as "maxCount does nothing here." The official
    Swagger spec (https://apidoc.borsdata.se/swagger/index.html — checked
    live, not just the wiki, which can be stale) explains why: this
    endpoint's `maxCount` is genuinely applied server-side, but it's "Max
    Year Count. Max 20" — a lookback window in **years**, not a row
    count. Confirmed live (instrumentId 352, 2026-09-12): omitting it
    defaults to 10 years (2,518 entries); `maxCount=1`/`5` return ~1/~5
    years (250/1,257 entries); `maxCount=0` returns nothing; any value
    ≥20 (200 included) clamps to the 20-year ceiling (4,960 entries, this
    instrument's entire history) — so 200 "looked" ignored only because
    it coincidentally clamped to the same result as no cap on this
    particular instrument, not because Börsdata skipped the parameter.
    Also confirmed live: `from`/`to`, when given, define the window
    directly and `maxCount` has no effect on top of them (a `from` 30
    days back plus `maxCount=1` still returned exactly those 30 entries,
    not a 1-year window) — `maxCount` only matters when `from`/`to` are
    omitted. Given all that, `GetStockPricesAsync`/`GetStockPrices` is a
    plain pass-through like `GetKpiHistory`/`GetKpiSummary` — no
    client-side capping — and the tool's own `[Description]` documents
    `maxCount`'s real years-based meaning so a caller reaching for "last
    30 days" knows to use `from`/`to` instead. An earlier version of this
    fix redefined `maxCount` as an entries-count and capped client-side
    after fetching the full history unfiltered — technically correct
    output, but wasteful (always downloading the whole history over the
    network even for a small request) and it hid a real, useful, already-
    documented server-side parameter behind an incorrect assumption; not
    worth resurrecting once the actual mechanism was understood.
  - `GetKpiScreener` (formerly named `GetKpiSummary` — renamed to match
    Börsdata's own terminology for `GET instruments/{insId}/kpis/{kpiId}/
    {calcGroup}/{calc}`, which their API wiki calls "KPI Screener": a
    single value for one specific KPI) must not be confused with the
    *actual* `GET instruments/{insId}/kpis/{reportType}/summary` endpoint,
    which Börsdata calls "summary" and returns **every** KPI for that
    instrument across multiple periods (confirmed live: 42 KPIs for Volvo
    B) — that's `GetKpiSummary` now. The two were easy to conflate since
    our old `GetKpiSummary` name (before this rename) actually pointed at
    the single-value screener endpoint, not Börsdata's own "summary"
    endpoint — matching neither Börsdata's own terminology. `GetKpiSummary`
    is a plain pass-through like `GetKpiHistory` — confirmed live that
    `maxCount` already caps periods-per-KPI server-side (27 → 2), so no
    client-side capping is needed. Confirmed live: this endpoint's `kpis`
    array uses `KpiId` (PascalCase), unlike every other endpoint's
    camelCase `kpiId` — yet another one-off naming inconsistency (like
    `kpiHistoryMetadatas` and stock-splits' `instrumentId`) left as Börsdata
    returned it rather than normalized.
  - `Tools/CalendarTools.cs` (a new file, separate from `MarketDataTools`/
    `ReferenceDataTools`) holds `GetReportCalendar` (`GET instruments/
    report/calendar?instList=...`)
    and `GetDividendCalendar` (`GET instruments/dividend/calendar?
    instList=...`). Confirmed live: both endpoints return each requested
    instrument's **entire** history in one list — both past dates and
    already-scheduled future ones — with no server-side `maxCount`, unlike
    `stockprices`/kpi history. A naive "take the first N" client-side cap
    would return the *oldest* N dates first, not the most useful ones,
    since the list is chronological ascending from a company's earliest
    tracked entry through its next scheduled one. Both tools instead take
    `fromDate`/`toDate` filters (confirmed live: passing today's date as
    `fromDate` correctly returns only upcoming dates, earliest-first) with
    `maxCount` applied *after* that filtering — so "next N" is an actual
    query, not just "N oldest entries." The
    two share one private `BuildCalendarResult` helper parameterized by
    the date field name (`releaseDate` vs `excludingDate` — confirmed
    live, the two endpoints use different field names for what's
    conceptually the same "when" despite an otherwise identical envelope
    shape) and the output array's key name (`reports` vs `dividends`).
  - `Tools/InstrumentLookup.cs` is a small internal (not `[McpServerToolType]`
    — it exposes no tools itself) static helper shared by `GetKpiListScreener`
    and `GetShortHoldings`: `BuildIndex` turns the cached instrument list
    into an `insId → (name, ticker)` lookup for enriching bare-`insId`
    results, and `ParseIds` parses a user-supplied comma-separated
    `instrumentIds` filter into a `HashSet<int>`. Extracted here once a
    second tool needed the identical logic — don't duplicate it a third
    time; add the parameter/behavior here instead.
  - `Tools/HoldingsTools.cs` holds `GetInsiderHoldings` (`GET holdings/
    insider?instList=...`), `GetBuybackHoldings` (`GET holdings/buyback?
    instList=...`), and `GetShortHoldings` (`GET holdings/shorts`,
    confirmed live to take **no** `instList` — it always returns every
    Nordic instrument, ~400+ entries, so `GetShortHoldings`'s
    `instrumentIds` filter is applied client-side after the fetch, unlike
    every other multi-instrument tool here where `instList` does the
    filtering server-side).
    - `GetInsiderHoldings` and `GetBuybackHoldings` share one private
      `BuildRecentFirstResult` helper (parameterized by the date field
      name — `transactionDate` vs `date` — the output array's key name,
      and an optional extra per-entry filter predicate) — the same
      envelope shape, oldest-first-with-no-server-side-filtering caveat,
      and "re-sort most-recent-first before capping" behavior as
      `CalendarTools.BuildCalendarResult`, just sorted descending instead
      of ascending since "recent activity" (not "next upcoming") is the
      natural need for holdings data. Confirmed live for buybacks too: a
      `maxCount` query param has no server-side effect (140 entries for
      Volvo B regardless) and instruments with no buybacks (e.g. Hacksaw)
      come back with an empty `values` array rather than being omitted.
    - `GetInsiderHoldings`: confirmed live that Börsdata's `transactionType`
      field does **not** follow a simple "1 = buy, 2 = sell" convention —
      live data for
      Volvo B shows values like `0, 3, 11, 13, 18, 19, 22, 25, 26, 38, 39`,
      and Börsdata's own API wiki documents no enumeration for this field.
      Don't resurrect a buy/sell mapping from `transactionType` without a
      confirmed source for what the codes mean. Instead, `direction`
      filters on the sign of the `shares` field (confirmed live: positive
      shares correlate with populated price/amount — an acquisition or
      equity grant; negative shares are disposals) — a reliable signal
      Börsdata does document implicitly through the data itself. This
      (and `minAmount`) is passed to `BuildRecentFirstResult` as the extra
      filter predicate, since `GetBuybackHoldings` has no equivalent.
    - `GetShortHoldings`: confirmed live `shortsProc` is stored as a
      negative number (e.g. `-7.69` for a 7.69% short) — a display
      convention, not a sign with meaning — so `minShortingPercent`
      filtering and the default sort both use `Math.Abs`. Defaults to
      descending (most-shorted-first) rather than the ascending default
      `GetKpiListScreener` uses, since "which stocks are most heavily
      shorted" is the overwhelmingly common screening question here,
      unlike a generic KPI where ascending vs. descending is metric-
      dependent.
- `Tools/` holds `[McpServerToolType]` static classes; each public static
  method tagged `[McpServerTool]` becomes one MCP tool. Tool methods take
  `BorsdataApiClient` as a parameter — the SDK resolves it (and other
  non-argument parameters like `CancellationToken`) from DI rather than
  from the client-supplied arguments. `[Description]` attributes on the
  method and its parameters become the tool/parameter descriptions surfaced
  to MCP clients — keep them accurate when changing behavior.
  - Optional parameters (e.g. `GetStockPrices`' `from`/`to`/`maxCount`)
    **must** have a C# default value (`= null`), not just a nullable type.
    Without one, the SDK's generated JSON schema lists the parameter in
    `required` anyway even though it's nullable — a real bug (some clients
    then can't omit it), not just style; see
    [csharp-sdk#1426](https://github.com/modelcontextprotocol/csharp-sdk/issues/1426).
  - Separately, nullable params still serialize as `"type": ["string",
    "null"]` rather than `anyOf`. That's spec-compliant JSON Schema
    2020-12, not a bug, and MCP Inspector's `--strict` schema-portability
    check flags it as a warning. Fixing it *is* possible — 
    `McpServerToolCreateOptions.SchemaCreateOptions`
    (`AIJsonSchemaCreateOptions.TransformSchemaNode`) can rewrite the
    schema — but only via manual per-tool `McpServerTool.Create(...)`
    registration; `WithToolsFromAssembly()` only accepts a
    `JsonSerializerOptions`, no way to plug in `SchemaCreateOptions`
    globally (verified via reflection against SDK 2.2.0). Deliberately
    left as-is: not worth trading away auto-discovery for a warning our
    actual clients (Claude, Inspector) already handle fine. Revisit if
    `WithToolsFromAssembly()` ever grows a `McpServerToolCreateOptions`
    overload, or if a client that actually chokes on it shows up.
- Endpoint/parameter names (`calcGroup`, `calc`, `kpiId`, report types like
  `year`/`quarter`/`r12`) follow Börsdata's own API reference
  (https://borsdata.se/en/insights/api) — check there before adding new
  endpoints rather than guessing at conventions.
- **No Docker.** It was tried and deliberately removed. Docker for a local
  stdio MCP server is a real pattern, but it's justified by sandboxing
  *someone else's* untrusted code (`npx`/`uvx` of a stranger's package) or
  by reproducibility across *many unknown users'* machines at
  distribution time — neither has applied here so far, since this is our
  own trusted code. All
  Docker bought us in practice was an extra manual rebuild step
  (`docker compose build`) every time source changed, which we repeatedly
  forgot and had to debug. If you're tempted to reintroduce it, that
  tradeoff hasn't changed unless the project starts actually distributing
  to other people's machines.
- **Distribution to other users is cleared with Börsdata.** We asked
  Börsdata directly whether publishing this project (so other people could
  run it against their own Börsdata data) was compatible with their API
  terms. Their answer (2026-09-14, via support): no problem, provided each
  user brings their **own Börsdata subscription and their own API key** —
  i.e. never a shared/bundled key serving multiple users' data under one
  account. This is already how the project works: the `.mcpb` extension
  collects the key per-installation through its own install UI (see
  `mcpb/` below), and `.env`/`appsettings.json` are per-developer-machine
  and gitignored/never-commit-a-real-key respectively. No architecture
  change was needed to satisfy this; it was already the shape of things
  before asking.
- `run-dev.sh` (Linux/macOS) / `run-dev.cmd` (Windows), repo root: source
  `.env` then `exec`/run `dotnet run --project src/BorsdataMcp`. This is
  what `.mcp.json`/`~/.claude.json` should point `command` at for local
  Code-tab registration (see `.mcp.json.example`) — always reflects the
  latest source with no separate build step, unlike the old Docker image.
- `mcpb/` packages the server as a `.mcpb` Desktop Extension, for Claude
  apps whose Chat/Cowork surface only reads MCP servers from a remote
  Connectors registry and can't see a local process registered via
  `.mcp.json`/`~/.claude.json` (that's a Code-tab-only mechanism in at
  least one observed app build). It bundles a framework-dependent
  `dotnet publish` of `src/BorsdataMcp` directly inside the `.mcpb`
  (output goes to `mcpb/server/app/`, gitignored — regenerate it before
  every `mcpb pack`, it's a build artifact like `mcpb/*.mcpb` itself, not
  checked in). Needs only the .NET **runtime** on the target machine.
  Because a framework-dependent publish is platform-neutral IL,
  `mcp_config.command` is just `"dotnet"` with the dll path as an arg —
  no wrapper scripts, no `platform_overrides` needed for this variant.
  The API key is collected by the extension's own install UI
  (`user_config` in `mcpb/manifest.json`), never read from `.env`.
  Declares `compatibility.platforms: ["linux", "darwin", "win32"]` but is
  only actually verified end-to-end on Linux here.
  `.github/workflows/build.yml` builds and packs it: every push to
  `main`/`workflow_dispatch` uploads a workflow artifact (a convenience
  build, not a release), while pushing a `v*` tag instead publishes a
  public GitHub Release — deliberately gated behind an intentional tag
  push rather than every commit. The committed `mcpb/manifest.json` has
  `"version": "0.0.0"` — a placeholder, not something to hand-edit; both
  jobs overwrite it with `jq` before packing rather than requiring a file
  to keep in sync by hand: `package` stamps `0.0.0+<short-sha>` (so two
  dev builds are distinguishable — they used to all ship as identical
  `0.0.0`s, making it impossible to tell which commit produced a given
  artifact), `release` stamps the tag's version.
  - `display_name` is `"Borsdata"` (plain ASCII, no "ö") as of 2026-09-14.
    A one-off failure in a Claude chat surface (`Tool
    'Börsdata:list_instruments' not found`, a literal escaped-unicode
    string apparently never decoded back before being used as a lookup
    key) briefly looked like the "ö" itself broke tool-name resolution
    there, and this session's own tool listing showing two differently-
    sanitized spellings of the same server (`mcp__B_rsdata__...` vs
    `mcp__borsdata__...`) seemed to back that up. `display_name` was
    changed to plain-ASCII `"Borsdata"` for a couple of commits on that
    theory, then reverted: rerunning the exact same failing query worked
    with no error and no other change, which at the time read as the
    failure being some other transient/stale-state glitch (most likely
    tied to the extension reinstall itself), not a deterministic bug tied
    to "ö". Left as `"Börsdata"` on that basis, with a note not to
    reflexively blame the display name again without a live retest.
    **That call didn't hold up**: the identical `Tool 'Börsdata:...'
    not found` failure recurred later (2026-09-14, reported by the user
    with a screenshot from a separate Claude chat surface, against the
    then-latest `main`), on a fresh `list_instruments` call this time —
    not a coincidental repeat of the earlier query. This session couldn't
    drive that chat surface to live-retest the same way the original
    investigation did, but it independently reproduced the *other* half of
    the original evidence live, in this session's own tool listing: the
    Claude Desktop app hosting this Code tab had both the installed
    `.mcpb` extension (registered as `"Börsdata"`, sanitized by whatever
    client-side logic to `B_rsdata`) and this repo's own local
    `.mcp.json` server (registered as `"borsdata"`, already plain ASCII)
    connected at once, surfacing as two separate tool prefixes for the
    same underlying server. Two independent live occurrences of the same
    specific symptom, both correlated with the non-ASCII `display_name`,
    was judged enough to act on even without a fresh in-chat-surface
    retest — `display_name` was changed back to plain-ASCII `"Borsdata"`.
    Root cause is still not fully nailed down (likely something in that
    chat surface's tool-search/dispatch pipeline unicode-escaping the
    qualified tool name for lookup without decoding it back, rather than
    anything Börsdata-API- or dotnet-side), and this is a workaround, not
    a fix for that pipeline. If it recurs *again* after this change ships
    in an installed extension, the "ö" theory is genuinely dead and the
    actual cause needs to be found elsewhere (e.g. via the client
    surface's own bug-report channel) rather than toggled back and forth
    a third time.
