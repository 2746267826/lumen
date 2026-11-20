#!/usr/bin/env bash
# 从 CHANGELOG.md 截取指定版本的段落，作为 GitHub Release 正文。
set -euo pipefail

tag="${1:?用法: extract-release-notes.sh <tag> [changelog]}"
changelog="${2:-CHANGELOG.md}"
version="${tag#v}"

if [ ! -f "$changelog" ]; then
  echo "找不到 $changelog" >&2
  exit 1
fi

notes="$(awk -v want="$version" '
  /^## / {
    heading = $0
    sub(/^## +/, "", heading)
    sub(/^v/, "", heading)
    sub(/[[:space:]].*$/, "", heading)
    printing = (heading == want)
    if (printing) { next }
  }
  printing { print }
' "$changelog")"

if [ -z "$(printf '%s' "$notes" | tr -d '[:space:]')" ]; then
  echo "CHANGELOG.md 中没有 $tag 的段落" >&2
  exit 1
fi

printf '%s\n' "$notes" | sed -e '/./,$!d'
