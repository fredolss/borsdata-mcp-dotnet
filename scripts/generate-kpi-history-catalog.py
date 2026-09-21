#!/usr/bin/env python3
"""Generate the bundled KPI history catalog from Börsdata's wiki Markdown table."""

import argparse
import json
from pathlib import Path


def parse_rows(markdown: str) -> list[dict]:
    options: list[dict] = []
    seen: set[tuple[int, str, str]] = set()

    for line_number, line in enumerate(markdown.splitlines(), 1):
        if not line.lstrip().startswith("|"):
            continue

        cells = [cell.strip() for cell in line.strip().strip("|").split("|")]
        if len(cells) != 5 or not cells[1].isdigit():
            continue

        kpi_name = cells[0]
        kpi_id = int(cells[1])
        report_type, price_type = cells[2], cells[3]
        description = " ".join(cells[4].split())
        if not kpi_name or not report_type or not price_type or not description:
            raise ValueError(f"Malformed KPI history row at line {line_number}: {line}")

        key = (kpi_id, report_type, price_type)
        if key in seen:
            raise ValueError(f"Duplicate KPI history combination at line {line_number}: {key}")
        seen.add(key)

        options.append(
            {
                "kpiId": kpi_id,
                "kpiName": kpi_name,
                "reportType": report_type,
                "priceType": price_type,
                "description": description,
            }
        )

    if not options:
        raise ValueError("No KPI history rows found in the Markdown input")
    return options


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=Path, help="Downloaded KPI-History.md")
    parser.add_argument("output", type=Path, help="Destination JSON file")
    parser.add_argument("--retrieved", required=True, help="Retrieval date in YYYY-MM-DD format")
    args = parser.parse_args()

    options = parse_rows(args.input.read_text(encoding="utf-8-sig"))
    document = {
        "schemaVersion": 1,
        "source": "https://github.com/Borsdata-Sweden/API/wiki/KPI-History",
        "retrieved": args.retrieved,
        "options": options,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    print(f"Wrote {len(options)} KPI history combinations to {args.output}")


if __name__ == "__main__":
    main()
