# Börsdata MCP Server

[![Build](https://github.com/fredolss/borsdata-mcp-dotnet/actions/workflows/build.yml/badge.svg)](https://github.com/fredolss/borsdata-mcp-dotnet/actions/workflows/build.yml)

A Model Context Protocol (MCP) server, written in .NET, that exposes the
[Börsdata](https://borsdata.se) financial data API (instruments, markets,
stock prices, KPIs, and financial reports) as MCP tools over stdio. Works
with any MCP client.

> **Disclaimer:** This is an unofficial, community-built project and is
> not affiliated with, endorsed by, or sponsored by Börsdata AB.
> "Börsdata" is a trademark of Börsdata AB, used here only to describe
> compatibility with their public API. You are responsible for your own
> Börsdata subscription, API key, and compliance with Börsdata's own
> terms of service. This software is provided as-is, without warranty of
> any kind — see [LICENSE](LICENSE).

## Example Queries

Once connected, you can ask your AI assistant things like this in swedish:

- "Vilka är de största bolagen på Nasdaq Stockholm efter börsvärde?
- "Jämför Handelsbanken och SEB - vilken är lägst värderad och lönsam?"
- "Visa top 10 bolag på Large cap som har lägst P/E ratio just nu."
- "Vilka svenska large-cap aktier har ett P/E under 15?"
- "Vilka bolag på small cap hade högsta trading volume denna vecka?"
- "Vilket bolag i finanssektorn har högsta vinstmarginal?"
- "Hur utvecklades Hacksaw jämfört med sektorsgenomsnittet förra året?"

## Tools

| Tool | Description | Parameters |
|---|---|---|
| `list_instruments` | Lists instruments (stocks/funds) on Börsdata with IDs, names, tickers, ISINs, and market/sector/branch/country refs. Returns `{ totalMatched, returned, instruments }` | `search`, `marketId`, `countryId`, `sectorId`, `branchId`, `includeGlobal`, `maxCount` (all optional; omitting every filter returns Börsdata's full instrument list — several thousand entries — so prefer `search`/id filters; `includeGlobal` merges in Börsdata's global/Pro+ instrument universe (~16,000 more), tagging each result `isGlobal`) |
| `list_markets` | Lists all markets known to Börsdata (e.g. Stockholm Large Cap, First North) | — |
| `list_branches` | Lists all industry branches known to Börsdata | — |
| `list_sectors` | Lists all sectors known to Börsdata | — |
| `list_countries` | Lists all countries known to Börsdata | — |
| `list_kpi_metadata` | Lists all KPIs known to Börsdata (kpiId, Swedish/English name, format, whether the value is a string) — use to find the `kpiId` for `get_kpi_screener`/`get_kpi_history`/`get_kpi_list_screener` | — |
| `get_stock_splits` | Stock splits and reverse splits across all instruments (small, ~44 entries live) — split date, ratio, type. Each result is enriched with `ticker`/`name` | — |
| `list_report_metadata` | Lists metadata for every field returned by `get_reports`/`get_kpi_summary` (property name, Swedish/English display name, format) — use to look up what a report field means | — |
| `list_translation_metadata` | Lists Börsdata's translation table (`translationKey` plus Swedish/English name) used for coded labels across the API, e.g. sector/branch names | — |
| `get_instruments_updated` | Last-updated timestamps for recently-updated instruments, most-recent-first. Does **not** cover every instrument — an id missing from the results just hasn't updated recently. Each result is enriched with `ticker`/`name`. Returns `{ totalMatched, returned, values }` | `instrumentIds` (comma-separated), `maxCount` (optional — omitting both returns ~700 entries) |
| `get_kpis_updated` | The single global timestamp for when Börsdata's KPI calculations were last refreshed (not per-instrument). Returns `{ kpisCalcUpdated }` | — |
| `get_stock_prices` | Daily stock price history (open, high, low, close, volume) for one instrument. Omitting `from`/`to`/`maxCount` returns Börsdata's default 10-year window. `maxCount` is a lookback window in **years** (1-20, Börsdata's own limit for this endpoint), not a count of days/entries — use `from`/`to` for an exact date range or a small recent window | `instrumentId` (required); `from`, `to`, `maxCount` (optional) |
| `get_latest_stock_prices` | The latest daily price for every instrument in one call. Each result is enriched with `ticker`/`name`. Returns `{ totalMatched, returned, values }` | `instrumentIds` (comma-separated), `global`, `maxCount` (optional — omitting both returns ~1,700 entries; `global` switches to Börsdata's non-Nordic Pro+ universe instead of the default Nordic one) |
| `get_stock_prices_by_date` | Every instrument's price on a specific historical date — same data as `get_latest_stock_prices` but for a chosen date; a non-trading day returns no results rather than an error | `date` (required); `instrumentIds` (comma-separated), `global`, `maxCount` (optional) |
| `get_kpi_screener` | A calculated KPI value (e.g. P/E, revenue growth) for one instrument — named to match Börsdata's own "KPI Screener" terminology for this endpoint | `instrumentId`, `kpiId`, `calcGroup`, `calc` (all required) |
| `get_kpi_history` | How a KPI (e.g. P/E) has trended over time for one instrument, unlike `get_kpi_screener`'s single current value | `instrumentId`, `kpiId`, `reportType`, `priceType` (required); `maxCount` (optional — not every reportType/priceType combination is valid for every KPI) |
| `get_kpi_summary` | Every KPI Börsdata tracks for one instrument across multiple periods in one call — unlike `get_kpi_screener`'s single value for one specific KPI. Each entry is keyed by `KpiId` (see `list_kpi_metadata` to resolve names) | `instrumentId`, `reportType` (`year`/`quarter`/`r12`) (required); `maxCount` (optional, caps periods per KPI) |
| `get_reports` | Financial reports (income statement, balance sheet, cash flow) for one instrument | `instrumentId`, `reportType` (`year`/`quarter`/`r12`) (all required) |
| `get_kpi_list_screener` | A calculated KPI value (e.g. P/E) for every instrument in one call — screen the whole market, or check one KPI across a specific set of holdings. Each result is enriched with `ticker`/`name`. Returns `{ kpiId, calcGroup, calc, totalMatched, returned, values }` | `kpiId`, `calcGroup`, `calc` (required); `instrumentIds` (comma-separated), `minValue`, `maxValue`, `sortDescending`, `global`, `maxCount` (optional — omitting all of these returns ~14,000 entries; `global` switches to Börsdata's non-Nordic Pro+ universe instead of the default Nordic one) |
| `get_report_calendar` | Report release dates for specified instruments — Börsdata returns each instrument's full past + already-scheduled history in one list, so pass `fromDate` (e.g. today) to get only upcoming reports. Returns `{ instruments: [{ insId, totalMatched, returned, reports }] }` | `instrumentIds` (comma-separated, required); `fromDate`, `toDate`, `maxCount` (optional) |
| `get_dividend_calendar` | Dividend ex-dates and amounts for specified instruments — same full past + already-scheduled history in one list as `get_report_calendar`, so pass `fromDate` for only upcoming dividends. Returns `{ instruments: [{ insId, totalMatched, returned, dividends }] }` | `instrumentIds` (comma-separated, required); `fromDate`, `toDate`, `maxCount` (optional) |
| `get_insider_holdings` | Insider transactions (board members/executives trading their own company's shares) for specified instruments, sorted most-recent-first. Use `direction` (based on the sign of shares) rather than Börsdata's undocumented `transactionType` codes to distinguish acquisitions from disposals. Returns `{ instruments: [{ insId, totalMatched, returned, transactions }] }` | `instrumentIds` (comma-separated, required); `fromDate`, `toDate`, `minAmount`, `direction` (`increase`/`decrease`), `maxCount` (optional) |
| `get_buyback_holdings` | Share buyback transactions for specified instruments, sorted most-recent-first (same full-history-oldest-first caveat as `get_insider_holdings`). Returns `{ instruments: [{ insId, totalMatched, returned, buybacks }] }` | `instrumentIds` (comma-separated, required); `fromDate`, `toDate`, `maxCount` (optional) |
| `get_short_holdings` | Short-position data (shorting %, holders, days-to-cover, trend) — covers every Nordic instrument in one call unless filtered. Sorted by shorting percent descending by default. Each result is enriched with `ticker`/`name`. Returns `{ totalMatched, returned, values }` | `instrumentIds` (comma-separated), `minShortingPercent`, `sortAscending`, `maxCount` (all optional — omitting all of these returns ~400+ entries) |

`instrumentId` is the `insId` returned by `list_instruments`, and is used
by every other tool that operates on a specific instrument.

## Requirements

- .NET 10 SDK (building/running from source) — the self-contained `.mcpb`
  Desktop Extension below needs no .NET installed at all; only the
  `-portable` variant needs the .NET 10 **runtime**
- Your own Börsdata account with API access ([borsdata.se](https://borsdata.se))
  and its API key. **This is required for every user, individually** — there
  is no shared or bundled key; each installation talks to Börsdata under its
  own account and subscription.

## Configuration

Set your API key via either:

- `src/BorsdataMcp/appsettings.json` (`Borsdata:ApiKey`), or
- the `Borsdata__ApiKey` environment variable (do not commit a real key to `appsettings.json`)

## Running

```bash
dotnet run --project src/BorsdataMcp
```

The server communicates over stdio, so it's normally launched by an MCP
client rather than run directly in a terminal — point your client's server
config at this command (or a published build).

`run-dev.sh` (Linux/macOS) / `run-dev.cmd` (Windows) do the same thing but
source `.env` first, so you don't have to export `Borsdata__ApiKey`
yourself:

```bash
cp .env.example .env   # then fill in Borsdata__ApiKey
./run-dev.sh
```

## Building and testing

```bash
dotnet build
dotnet test
```

## Using with Claude Desktop

Two Claude-specific ways to register this server, on top of the generic
stdio usage above.

**Local server config:** copy `.mcp.json.example` to `.mcp.json` (already
gitignored — it holds an absolute, machine-specific path) and point
`command` at `run-dev.sh`/`run-dev.cmd`.

**Desktop Extension (`.mcpb`):** for Claude apps whose Chat/Cowork surface
only reads MCP servers from a remote Connectors registry, not a local
config file. A [Desktop Extension](https://claude.com/docs/connectors/building/mcpb)
is a small zip, installed via drag-and-drop into Settings → Extensions,
that bundles this server plus a manifest telling Claude how to launch it
and what to ask for (the API key) at install time.

Each [Release](../../releases) ships four `.mcpb` files — pick one:

- **`borsdata-mcp-<version>-linux-x64.mcpb`** — self-contained, Linux only.
  Bundles the .NET runtime itself, so nothing needs to be installed
  separately. Recommended for most Linux users. (Confirmed end-to-end on
  Linux.)
- **`borsdata-mcp-<version>-win-x64.mcpb`** — self-contained, Windows only.
  Same as above but for Windows; not yet verified end-to-end there.
- **`borsdata-mcp-<version>-osx-universal.mcpb`** — self-contained, macOS
  only, a universal binary covering both Intel and Apple Silicon Macs. Not
  yet verified end-to-end there.
- **`borsdata-mcp-<version>-portable.mcpb`** — framework-dependent, works on
  Linux/macOS/Windows, but requires the .NET 10 **runtime** (not the SDK)
  to already be installed on the machine. Smaller download; an alternative
  to the self-contained builds above for anyone who already has .NET
  installed.

Or build any of these yourself:

```bash
# portable (framework-dependent, needs .NET 10 runtime installed)
dotnet publish src/BorsdataMcp/BorsdataMcp.csproj -c Release -o mcpb/server/app
cd mcpb && npx --yes @anthropic-ai/mcpb pack   # produces mcpb.mcpb

# self-contained (bundles the runtime; substitute win-x64 for Windows)
dotnet publish src/BorsdataMcp/BorsdataMcp.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o mcpb/server/app
# then edit mcpb/manifest.json's server.entry_point/mcp_config.command to
# "server/app/BorsdataMcp" (or "server/app/BorsdataMcp.exe" for win-x64) —
# see .github/workflows/build.yml for the exact jq patch CI applies —
# before packing

# self-contained universal macOS build (run on an actual Mac — needs `lipo`)
dotnet publish src/BorsdataMcp/BorsdataMcp.csproj -c Release -r osx-x64 --self-contained true -p:PublishSingleFile=true -o publish-osx-x64
dotnet publish src/BorsdataMcp/BorsdataMcp.csproj -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -o publish-osx-arm64
mkdir -p mcpb/server/app
lipo -create -output mcpb/server/app/BorsdataMcp publish-osx-x64/BorsdataMcp publish-osx-arm64/BorsdataMcp
cp publish-osx-x64/appsettings.json mcpb/server/app/appsettings.json
# then edit mcpb/manifest.json the same way as above (entry_point/command to
# "server/app/BorsdataMcp", compatibility.platforms to ["darwin"]) before packing
```

Drag it into Settings → Extensions and enter your Börsdata API key when
prompted (collected by the install UI, not read from `.env`).

> **You need your own Börsdata account with API access to use this
> extension.** It does not come with a Börsdata subscription or API key —
> every installation requires the user's own [borsdata.se](https://borsdata.se)
> account and key entered at install time; there is no shared key.

## Project layout

- `src/BorsdataMcp` — the MCP server: `Program.cs` wires up hosting, DI, and
  the stdio transport; `BorsdataApiClient` wraps the Börsdata HTTP API;
  `AuthKeyHandler` attaches the API key to every outgoing request;
  `Tools/` contains the `[McpServerTool]`-attributed methods exposed to
  clients.
- `tests/BorsdataMcp.Tests` — unit tests.
- `mcpb/` — packages the server as a Claude Desktop Extension; see above.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for how to get set up and submit changes.

## License

[MIT](LICENSE)
