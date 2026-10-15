#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
arch="${1:-$(uname -m)}"
case "$arch" in arm64|osx-arm64) arch=arm64; rid=osx-arm64;; x86_64|osx-x64) arch=x86_64; rid=osx-x64;; *) echo 'Unsupported Mac architecture' >&2; exit 1;; esac
out="$root/src/URemote.Module/Native/bin/$rid"
mkdir -p "$out"
xcrun clang -arch "$arch" -mmacosx-version-min=12.3 -fobjc-arc -fblocks -dynamiclib -O2 -Wall -Wextra \
  "$root/src/URemote.Linux/Mac/desktop.m" -framework AppKit -framework ScreenCaptureKit -framework CoreMedia \
  -framework CoreVideo -framework ApplicationServices -install_name @rpath/liburemote-mac.dylib -o "$out/liburemote-mac.dylib"
codesign --force --sign - "$out/liburemote-mac.dylib"
