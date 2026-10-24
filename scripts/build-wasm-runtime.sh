#!/usr/bin/env bash
# Builds AquariusCore/wasm/runtime/runtime.wasm on Linux/WSL/macOS with the pinned LLVM/Clang 23.1.3 toolchain.
# Mirrors scripts/build-wasm-runtime.ps1 flag for flag, so both produce the committed bytes.
# Install the toolchain with `pwsh scripts/install-wasm-toolchain.ps1`, or set AQUARIUS_LLVM to an LLVM 23.1.3 root.
#   scripts/build-wasm-runtime.sh [--check | output.wasm]
set -euo pipefail
repository=$(cd "$(dirname "$0")/.." && pwd)
source="$repository/AquariusCore/wasm/runtime/runtime.c"
committed="$repository/AquariusCore/wasm/runtime/runtime.wasm"
check=0; output=$committed
if [ "${1:-}" = "--check" ]; then check=1; output="$repository/.native-build/runtime-check.wasm"; elif [ -n "${1:-}" ]; then output=$1; fi
case "$(uname -s)-$(uname -m)" in
    Linux-x86_64) platform=linux-x64 ;;
    Darwin-arm64) platform=macos-arm64 ;;
    *) echo "The pinned LLVM toolchain is published for Linux x64 and macOS arm64 (and Windows x64)." >&2; exit 1 ;;
esac
llvm=${AQUARIUS_LLVM:-"$repository/.native-build/toolchains/llvm-23.1.3-$platform"}
clang="$llvm/bin/clang"
if [ ! -x "$clang" ]; then echo "Pinned clang not found at $clang. Run: pwsh scripts/install-wasm-toolchain.ps1" >&2; exit 1; fi
if ! "$clang" --version | head -n1 | grep -q 'clang version 23\.1\.3\b'; then echo "Expected clang 23.1.3: $("$clang" --version | head -n1)" >&2; exit 1; fi
mkdir -p "$(dirname "$output")"
exports=()
for name in $( { cat "$source" "$(dirname "$source")/threads.h" | grep -oE '\b(u32|int)[[:space:]]+(aqua_|rt_)[A-Za-z0-9_]+[[:space:]]*\(' | sed -E 's/^(u32|int)[[:space:]]+//; s/[[:space:]]*\($//';
        for op in add subtract multiply divide less greater lessEqual greaterEqual equal notEqual and or modulo; do echo "rt_$op"; done; } | LC_ALL=C sort -u); do
    exports+=("-Wl,--export=$name")
done
"$clang" --target=wasm32-unknown-unknown -mcpu=lime1 -mbulk-memory -O3 -g0 -std=c11 -fno-builtin -nostdlib \
    -Wl,--no-entry -Wl,--export-table -Wl,--strip-all "${exports[@]}" \
    -Wl,-z,stack-size=1048576 -Wl,--initial-memory=16777216 -Wl,--max-memory=536870912 \
    "$source" -o "$output"
hash() { if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }
if [ $check = 1 ] && [ "$(hash "$output")" != "$(hash "$committed")" ]; then
    echo "The committed runtime.wasm differs from runtime.c. Rebuild it with the pinned Clang and commit the result." >&2; exit 1
fi
echo "Built $output: $(hash "$output")"
