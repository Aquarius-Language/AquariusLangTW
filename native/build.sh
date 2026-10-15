#!/usr/bin/env bash
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
[[ $(uname -s) == Linux ]] || { echo 'This script builds the Linux desktop bridges.' >&2; exit 1; }
case "$(uname -m)" in
    x86_64) runtime=linux-x64 ;;
    *) echo 'The Linux desktop runtime currently supports x86_64.' >&2; exit 1 ;;
esac
cmake -S "$root/native" -B "$root/native/build/$runtime" -DCMAKE_BUILD_TYPE=Release "$@"
cmake --build "$root/native/build/$runtime" --parallel "${AQUARIUS_BUILD_JOBS:-4}"
cmake --install "$root/native/build/$runtime" --prefix "$root/AquariusDesktop/runtimes/$runtime/native"
