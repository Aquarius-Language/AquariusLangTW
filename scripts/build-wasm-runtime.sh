#!/usr/bin/env bash
# Builds AquariusCore/wasm/runtime/runtime.wasm on Linux/WSL/macOS.
# Mirrors scripts/build-wasm-runtime.ps1. Uses `zig cc` (pinned 0.15.1, preferred for
# reproducible checked-in bytes) or any clang with the wasm32 target and wasm-ld.
#   scripts/build-wasm-runtime.sh [output.wasm]
set -euo pipefail
repository=$(cd "$(dirname "$0")/.." && pwd)
source="$repository/AquariusCore/wasm/runtime/runtime.c"
output=${1:-"$repository/AquariusCore/wasm/runtime/runtime.wasm"}
if [ -n "${AQUARIUS_WASM_CC:-}" ]; then compiler=($AQUARIUS_WASM_CC)
elif command -v zig >/dev/null 2>&1; then compiler=(zig cc --target=wasm32-freestanding)
else compiler=(clang --target=wasm32-unknown-unknown); fi
exports=()
for name in $(cat "$source" "$(dirname "$source")/threads.h" | grep -oE '\b(u32|int)[[:space:]]+(aqua_|rt_)[A-Za-z0-9_]+[[:space:]]*\(' | sed -E 's/^(u32|int)[[:space:]]+//; s/[[:space:]]*\($//' | sort -u); do
    exports+=("-Wl,--export=$name")
done
for name in add subtract multiply divide less greater lessEqual greaterEqual equal notEqual and or modulo; do exports+=("-Wl,--export=rt_$name"); done
"${compiler[@]}" -O3 -g0 -std=c11 -fno-builtin -mbulk-memory -nostdlib \
    -Wl,--no-entry -Wl,--export-table -Wl,--strip-all "${exports[@]}" \
    -Wl,-z,stack-size=1048576 -Wl,--initial-memory=16777216 -Wl,--max-memory=536870912 \
    "$source" -o "$output"
echo "Built $output: $(sha256sum "$output" | cut -d' ' -f1)"
