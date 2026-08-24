#!/usr/bin/env bash
set -euo pipefail

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet SDK is required. Install .NET 10 SDK first." >&2
  exit 1
fi

dotnet --info

dotnet restore

dotnet build -c Release

echo "StoneAge Linux bootstrap complete."
