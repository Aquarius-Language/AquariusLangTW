#!/usr/bin/env bash
# Linux/macOS twin of install-toolchain.ps1 for hosts without PowerShell: installs the pinned toolchain described in
# scripts/toolchain.json (read with node, which the repository already requires) into the gitignored .native-build:
# the official LLVM release, the wasi-sdk WASI sysroot and compiler-rt, CMake and Ninja, a JDK for Android targets, and the prebuilt
# Wasmtime C API for each requested target. Every archive's SHA-256 is verified. Prints clang's path.
#   scripts/install-toolchain.sh [linux-x64,android-arm64-v8a,android-x86_64,web]   (default: this host's target)
set -euo pipefail
repository=$(cd "$(dirname "$0")/.." && pwd)
pins="$repository/scripts/toolchain.json"
case "$(uname -s)-$(uname -m)" in
    Linux-x86_64) platform=linux-x64 ;;
    Darwin-arm64) platform=macos-arm64 ;;
    *) echo "The pinned LLVM toolchain is published for Linux x64 and macOS arm64 (and Windows x64)." >&2; exit 1 ;;
esac
targets=${1:-$platform}
pin() { node -e "const p=require(process.argv[1]);const v=process.argv[2].split('.').reduce((o,k)=>o?.[k],p);process.stdout.write(v===undefined?'':String(v).replace('{platform}',process.argv[3]))" "$pins" "$1" "$platform"; }
native=${AQUARIUS_NATIVE_BUILD:-"$repository/.native-build"}
toolchains="$native/toolchains"; sdks="$native/sdk"; mkdir -p "$toolchains" "$sdks"
sha() { if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }
install_archive() { # url sha256 destination: an archive with one top-level folder becomes that folder
    local url=$1 expected=$2 destination=$3 archive staging
    [ -d "$destination" ] && return 0
    archive="$toolchains/$(basename "$url")"
    [ -f "$archive" ] || curl -fL --retry 4 -o "$archive" "$url"
    if [ "$(sha "$archive")" != "$expected" ]; then rm -f "$archive"; echo "Checksum mismatch for $url" >&2; exit 1; fi
    staging=$(mktemp -d "$toolchains/.unpack-XXXXXX")
    # .zip archives (Ninja's): unzip, or the pinned CMake, which is installed first, where unzip is missing.
    case "$archive" in
        *.zip) if command -v unzip >/dev/null 2>&1; then unzip -q "$archive" -d "$staging"; else (cd "$staging" && "$cmake" -E tar xf "$archive"); fi ;;
        *) tar -xf "$archive" -C "$staging" ;;
    esac
    mkdir -p "$(dirname "$destination")"
    if [ "$(ls -A "$staging" | wc -l)" = 1 ] && [ -d "$staging/$(ls -A "$staging")" ]; then mv "$staging"/* "$destination"; rmdir "$staging"
    else mv "$staging" "$destination"; fi
    rm -f "$archive"
}
llvm=${AQUARIUS_LLVM:-"$toolchains/$(pin llvm.directory)"}
[ -n "${AQUARIUS_LLVM:-}" ] || install_archive "$(pin llvm.base)$(pin "llvm.$platform.file")" "$(pin "llvm.$platform.sha256")" "$llvm"
install_archive "$(pin sysroot.url)" "$(pin sysroot.sha256)" "$toolchains/$(pin sysroot.directory)"
install_archive "$(pin compilerRt.url)" "$(pin compilerRt.sha256)" "$toolchains/$(pin compilerRt.directory)"
# As wasi-sdk documents, the WebAssembly compiler-rt builtins belong in clang's resource directory.
resource=$("$llvm/bin/clang" --target=wasm32-wasip1 -print-resource-dir)
for target in "$toolchains/$(pin compilerRt.directory)"/*/; do
    [ -d "$resource/lib/$(basename "$target")" ] || cp -R "$target" "$resource/lib/$(basename "$target")"
done
cmake="$toolchains/$(pin cmake.directory)/$(pin "cmake.$platform.bin")/cmake"
for tool in cmake ninja; do
    install_archive "$(pin $tool.base)$(pin "$tool.$platform.file")" "$(pin "$tool.$platform.sha256")" "$toolchains/$(pin $tool.directory)"
done
chmod +x "$toolchains/$(pin ninja.directory)/ninja"   # .zip archives may carry no permissions
for target in ${targets//,/ }; do
    case "$target" in
        web) continue ;;
        android-*) install_archive "$(pin jdk.base)$(pin "jdk.$platform.file")" "$(pin "jdk.$platform.sha256")" "$toolchains/$(pin jdk.directory)" ;;
    esac
    file=$(pin "sdks.wasmtime.$target.file")
    [ -n "$file" ] || { echo "No pinned Wasmtime SDK for $target." >&2; exit 1; }
    install_archive "$(pin sdks.wasmtime.base)$file" "$(pin "sdks.wasmtime.$target.sha256")" "$sdks/wasmtime-$(pin sdks.wasmtime.version)-$target"
done
"$llvm/bin/clang" --version | head -n1 | grep -q "clang version $(pin llvm.version)\b" || { echo "Unexpected clang version" >&2; exit 1; }
echo "$llvm/bin/clang"
