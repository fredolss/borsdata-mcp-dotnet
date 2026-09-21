#!/usr/bin/env python3
"""Generate the bundled KPI screener catalog from Börsdata's wiki Markdown table."""

import argparse
import json
import re
from pathlib import Path


NAME_PATTERN = re.compile(r"^\[([^]]+)]")


def parse_rows(markdown: str) -> list[dict]:
    raw_rows: list[tuple[int, str, str, str, str | None, int]] = []
    seen: set[tuple[int, str, str]] = set()

    for line_number, line in enumerate(markdown.splitlines(), 1):
        if not line.lstrip().startswith("|"):
            continue

        cells = [cell.strip() for cell in line.strip().strip("|").split("|")]
        if len(cells) != 4 or not cells[0].isdigit():
            continue

        kpi_id = int(cells[0])
        calc_group, calc = cells[1], cells[2]
        description = " ".join(cells[3].split())
        name_match = NAME_PATTERN.match(description)
        if not calc_group or not calc or not description:
            raise ValueError(f"Malformed KPI row at line {line_number}: {line}")

        key = (kpi_id, calc_group, calc)
        if key in seen:
            raise ValueError(f"Duplicate KPI combination at line {line_number}: {key}")
        seen.add(key)

        raw_rows.append(
            (kpi_id, calc_group, calc, description,
             name_match.group(1).strip() if name_match else None, line_number)
        )

    if not raw_rows:
        raise ValueError("No KPI rows found in the Markdown input")

    names_by_id = {
        kpi_id: name
        for kpi_id, _, _, _, name, _ in raw_rows
        if name is not None
    }
    options: list[dict] = []
    for kpi_id, calc_group, calc, description, name, line_number in raw_rows:
        resolved_name = name or names_by_id.get(kpi_id)
        if resolved_name is None:
            raise ValueError(f"No KPI name available for row at line {line_number}")
        options.append(
            {
                "kpiId": kpi_id,
                "kpiName": resolved_name,
                "calcGroup": calc_group,
                "calc": calc,
                "description": description,
            }
        )
    return options


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=Path, help="Downloaded Kpi-Screener-List.md")
    parser.add_argument("output", type=Path, help="Destination JSON file")
    parser.add_argument("--retrieved", required=True, help="Retrieval date in YYYY-MM-DD format")
    args = parser.parse_args()

    options = parse_rows(args.input.read_text(encoding="utf-8-sig"))
    document = {
        "schemaVersion": 1,
        "source": "https://github.com/Borsdata-Sweden/API/wiki/Kpi-Screener-List",
        "retrieved": args.retrieved,
        "options": options,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    print(f"Wrote {len(options)} KPI combinations to {args.output}")


if __name__ == "__main__":
    main()
