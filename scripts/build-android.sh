#!/usr/bin/env sh
set -eu

ROOT="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
cd "$ROOT/src/Lumen.Android"

./gradlew :app:assembleRelease

APK="$ROOT/src/Lumen.Android/app/build/outputs/apk/release/app-release.apk"
if [ ! -f "$APK" ]; then
    APK="$ROOT/src/Lumen.Android/app/build/outputs/apk/release/app-release-unsigned.apk"
fi

printf '\nAPK: %s\n' "$APK"
