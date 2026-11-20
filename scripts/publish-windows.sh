#!/usr/bin/env sh
set -eu

ROOT="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
OUT="${1:-$ROOT/artifacts/windows}"
SELF="$OUT/self-contained"
FRAMEWORK="$OUT/framework-dependent"

dotnet publish "$ROOT/src/Lumen.Windows/Lumen.Windows.csproj" \
    -c Release \
    -r win-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    -o "$SELF"

dotnet publish "$ROOT/src/Lumen.Windows/Lumen.Windows.csproj" \
    -c Release \
    -r win-x64 \
    --self-contained false \
    -p:PublishSingleFile=true \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    -o "$FRAMEWORK"

printf '\nSelf-contained build: %s\n' "$SELF"
printf 'Framework-dependent build: %s\n' "$FRAMEWORK"
