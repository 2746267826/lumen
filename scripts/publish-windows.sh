#!/usr/bin/env sh
set -eu

ROOT="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
OUT="${1:-$ROOT/artifacts/windows}"

dotnet publish "$ROOT/src/Lumen.Windows/Lumen.Windows.csproj" \
    -c Release \
    -r win-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$OUT"

printf '\nWindows build written to: %s\n' "$OUT"
