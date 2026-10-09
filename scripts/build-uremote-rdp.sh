#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
case "$(uname -s):$(uname -m)" in
  Linux:x86_64) RID=linux-x64 ;;
  Linux:aarch64|Linux:arm64) RID=linux-arm64 ;;
  Darwin:x86_64) RID=osx-x64 ;;
  Darwin:arm64) RID=osx-arm64 ;;
  *) echo "Unsupported native RDP build platform" >&2; exit 2 ;;
esac
OUTPUT="${1:-$ROOT/src/URemote.Module/Native/bin/$RID}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
cmake -S "$ROOT/src/URemote.Module/Native" -B "$WORK" -DCMAKE_BUILD_TYPE=Release
cmake --build "$WORK" --parallel 2
cmake --install "$WORK" --prefix "$OUTPUT"
