#!/usr/bin/env bash
# Linux check (Ubuntu or WSL): builds the linux-x64 runtime pack with the pinned LLVM (AQUARIUS_LLVM, see
# scripts/install-toolchain.sh), then builds and tests the compiler against it.
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$root"
build=${AQUARIUS_RUNTIME_BUILD:-"$root/.native-build/build/linux-x64"}
cmake -S runtime -B "$build" -G 'Unix Makefiles' -DCMAKE_TOOLCHAIN_FILE="$root/runtime/sdk/cmake/toolchains/linux-x64.cmake" -DCMAKE_BUILD_TYPE=Release
cmake --build "$build" -j"$(nproc)"
rm -rf .native-build/install/linux-x64
cmake --install "$build" --prefix .native-build/install/linux-x64
dotnet build AquariusLang.sln -m:1 --nologo
dotnet test compiler/AquariusTests -m:1 --no-build --logger 'trx;LogFileName=linux.trx'
