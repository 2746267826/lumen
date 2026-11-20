#!/usr/bin/env sh
set -eu

ROOT="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
OUT="${1:-$ROOT/artifacts/windows}"
SELF="$OUT/self-contained"
FRAMEWORK="$OUT/framework-dependent"
VERSION="${2:-$(git -C "$ROOT" describe --tags --abbrev=0 2>/dev/null | sed 's/^v//')}"
VERSION="${VERSION:-dev}"

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

mv "$SELF/Lumen.Windows.exe" "$SELF/Lumen-$VERSION.exe"
mv "$FRAMEWORK/Lumen.Windows.exe" "$FRAMEWORK/Lumen-$VERSION.exe"

printf '\nSelf-contained build: %s\n' "$SELF"
printf 'Framework-dependent build: %s\n' "$FRAMEWORK"
