#!/usr/bin/env bash
# Linux/macOS twin of install-wasm-toolchain.ps1 for hosts without PowerShell: installs the pinned LLVM/Clang toolchain
# described in scripts/wasm-toolchain.json (read with node, which the repository already requires) into the gitignored
# .native-build/toolchains, verifying every archive's SHA-256. Prints clang's path.
set -euo pipefail
repository=$(cd "$(dirname "$0")/.." && pwd)
pins="$repository/scripts/wasm-toolchain.json"
case "$(uname -s)-$(uname -m)" in
    Linux-x86_64) platform=linux-x64 ;;
    Darwin-arm64) platform=macos-arm64 ;;
    *) echo "The pinned LLVM toolchain is published for Linux x64 and macOS arm64 (and Windows x64)." >&2; exit 1 ;;
esac
pin() { node -e "const p=require(process.argv[1]);const v=process.argv[2].split('.').reduce((o,k)=>o[k],p);process.stdout.write(String(v).replace('{platform}',process.argv[3]))" "$pins" "$1" "$platform"; }
toolchains="$repository/.native-build/toolchains"; mkdir -p "$toolchains"
sha() { if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }
install_archive() { # url sha256 destination
    local url=$1 expected=$2 destination=$3 archive staging
    [ -d "$destination" ] && return 0
    archive="$toolchains/$(basename "$url")"
    [ -f "$archive" ] || curl -fL --retry 4 -o "$archive" "$url"
    if [ "$(sha "$archive")" != "$expected" ]; then rm -f "$archive"; echo "Checksum mismatch for $url" >&2; exit 1; fi
    staging=$(mktemp -d "$toolchains/.unpack-XXXXXX")
    tar -xf "$archive" -C "$staging"
    if [ "$(ls -A "$staging" | wc -l)" != 1 ]; then rm -rf "$staging"; echo "Unexpected layout in $archive" >&2; exit 1; fi
    mv "$staging"/* "$destination"; rmdir "$staging"; rm -f "$archive"
}
llvm=${AQUARIUS_LLVM:-"$toolchains/$(pin llvm.directory)"}
[ -n "${AQUARIUS_LLVM:-}" ] || install_archive "$(pin llvm.base)$(pin "llvm.$platform.file")" "$(pin "llvm.$platform.sha256")" "$llvm"
install_archive "$(pin sysroot.url)" "$(pin sysroot.sha256)" "$toolchains/$(pin sysroot.directory)"
install_archive "$(pin compilerRt.url)" "$(pin compilerRt.sha256)" "$toolchains/$(pin compilerRt.directory)"
resource=$("$llvm/bin/clang" --target=wasm32-wasip1 -print-resource-dir)
for target in "$toolchains/$(pin compilerRt.directory)"/*/; do
    [ -d "$resource/lib/$(basename "$target")" ] || cp -R "$target" "$resource/lib/$(basename "$target")"
done
"$llvm/bin/clang" --version | head -n1 | grep -q "clang version $(pin llvm.version)\b" || { echo "Unexpected clang version" >&2; exit 1; }
echo "$llvm/bin/clang"
