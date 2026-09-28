export const github = "https://github.com/fredolss/borsdata-mcp-dotnet";
export interface ToolGroup {
  title: string;
  tools: { name: string; description: string }[];
}
export const toolGroups: ToolGroup[] = [
  {
    title: "Instrument",
    tools: [
      {
        name: "search_instruments",
        description:
          "Sök bolag med namn, ticker, ISIN eller marknads- och branschfilter.",
      },
      {
        name: "list_markets",
        description: "Se marknaderna som finns i Börsdata.",
      },
      {
        name: "list_branches",
        description: "Lista branscher.",
      },
      {
        name: "list_sectors",
        description: "Lista sektorer.",
      },
      {
        name: "list_countries",
        description: "Lista länder.",
      },
      {
        name: "get_instrument_descriptions",
        description: "Hämta bolagsbeskrivningar för upp till 50 instrument.",
      },
      {
        name: "get_instruments_updated",
        description: "Se när nyligen ändrade instrument uppdaterades.",
      },
      {
        name: "list_translation_metadata",
        description: "Slå upp svenska och engelska benämningar.",
      },
    ],
  },
  {
    title: "Kurser",
    tools: [
      {
        name: "get_stock_prices",
        description: "Hämta daglig kurshistorik för ett instrument.",
      },
      {
        name: "get_latest_stock_prices",
        description: "Hämta de senast tillgängliga dagskurserna.",
      },
      {
        name: "get_stock_prices_by_date",
        description: "Hämta dagskurser för ett visst datum.",
      },
      {
        name: "get_stock_splits",
        description: "Se aktiesplittar och omvända splittar.",
      },
    ],
  },
  {
    title: "Nyckeltal",
    tools: [
      {
        name: "list_kpi_metadata",
        description: "Hitta nyckeltal och deras ID:n.",
      },
      {
        name: "list_kpi_history_options",
        description: "Slå upp giltiga kombinationer för KPI-historik.",
      },
      {
        name: "get_kpi_screener",
        description: "Hämta ett beräknat nyckeltal för ett instrument.",
      },
      {
        name: "get_kpi_history",
        description: "Följ ett nyckeltal över tid.",
      },
      {
        name: "get_kpi_history_array",
        description: "Hämta KPI-historik för upp till 50 instrument.",
      },
      {
        name: "get_kpi_summary",
        description: "Hämta ett instruments nyckeltal över flera perioder.",
      },
      {
        name: "get_kpis_updated",
        description: "Se när KPI-beräkningarna senast uppdaterades.",
      },
    ],
  },
  {
    title: "Screening",
    tools: [
      {
        name: "screen_instruments",
        description: "Hitta instrument som uppfyller flera KPI-villkor.",
      },
      {
        name: "list_kpi_screener_options",
        description: "Slå upp giltiga KPI-beräkningar för screening.",
      },
      {
        name: "get_kpi_list_screener",
        description:
          "Hämta en komplett nordisk KPI-lista för egen bearbetning.",
      },
      {
        name: "get_global_kpi_list_screener",
        description: "Hämta motsvarande kompletta globala KPI-lista.",
      },
    ],
  },
  {
    title: "Rapporter",
    tools: [
      {
        name: "get_reports",
        description: "Hämta års-, kvartals- eller R12-rapporter.",
      },
      {
        name: "get_reports_compound",
        description: "Hämta alla rapporttyper för ett instrument.",
      },
      {
        name: "get_reports_array",
        description: "Hämta alla rapporttyper för flera instrument.",
      },
      {
        name: "list_report_metadata",
        description: "Slå upp rapportfältens namn och format.",
      },
    ],
  },
  {
    title: "Ägande & blankning",
    tools: [
      {
        name: "get_insider_holdings",
        description: "Hämta insidertransaktioner, inklusive tilldelningar.",
      },
      {
        name: "get_buyback_holdings",
        description: "Hämta bolagens aktieåterköp.",
      },
      {
        name: "get_short_holdings",
        description: "Se blankningspositioner för nordiska instrument.",
      },
    ],
  },
  {
    title: "Kalender & utdelningar",
    tools: [
      {
        name: "get_report_calendar",
        description: "Hämta historiska och planerade rapportdatum.",
      },
      {
        name: "get_dividend_calendar",
        description: "Hämta utdelningarnas ex-datum och belopp.",
      },
    ],
  },
];
