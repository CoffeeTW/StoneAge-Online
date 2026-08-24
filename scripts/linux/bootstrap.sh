#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$ROOT/src/StoneAge.Server/StoneAge.Server.csproj"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet SDK is required. Install .NET 10 SDK first." >&2
  exit 1
fi

dotnet --info
dotnet restore "$PROJECT"
dotnet build "$PROJECT" -c Release --no-restore

echo "StoneAge Linux bootstrap complete."
