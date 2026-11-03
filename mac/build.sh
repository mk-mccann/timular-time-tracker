#!/bin/sh
# Compiles the CoreBluetooth helper (needs Xcode command line tools: xcode-select --install).
# Usage: mac/build.sh <output-dir>
set -e
cd "$(dirname "$0")"
out="${1:-.}"
mkdir -p "$out"
swiftc -O zei-ble.swift -o "$out/zei-ble" \
  -Xlinker -sectcreate -Xlinker __TEXT -Xlinker __info_plist -Xlinker Info.plist
