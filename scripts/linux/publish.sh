#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OUT="${1:-$ROOT/artifacts/linux-x64}"

rm -rf "$OUT"
mkdir -p "$OUT"

dotnet publish "$ROOT/src/StoneAge.Server/StoneAge.Server.csproj" \
  -c Release \
  -r linux-x64 \
  --self-contained false \
  -o "$OUT"

echo "Published to $OUT"
