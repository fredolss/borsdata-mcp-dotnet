#!/bin/sh
set -a
. "$(dirname "$0")/.env"
set +a
exec dotnet run --project "$(dirname "$0")/src/BorsdataMcp"
