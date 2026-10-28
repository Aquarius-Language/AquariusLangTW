# Performance roadmap round — 2026-10-28

Branch `claude/project-thread-wcj2ja` (this session may push only to its assigned branch, so the work is not on
`claude/perf-roadmap`). It is eight commits on top of `main` (`fb80753`). Every item follows CLAUDE.md. Except one
engine-layer change (the runtime JIT's code cache, `runtime/host/engine_wasmtime.c`), every optimization lives in the
compiler or the guest runtime, so it is portable: the same `.wasm` gains on desktop, web and Android. Each is checked
against the unoptimized compiler (the differential `EmitterTiers` tests), under tiny fuel quanta and with the JIT on and
off.

## Summary

| Mode | Language | Score before | Score after | Before vs Node.js | After vs Node.js |
| --- | --- | ---: | ---: | ---: | ---: |
| Standard | Untyped 星泉 | 16.74 | **49.33** | 0.46× | **1.31×** |
| Standard | Typed 星泉（型別） | 50.02 | 52.66 | 1.38× | 1.39× |
| Warmed | Untyped 星泉 | 14.14 | **38.99** | 0.30× | **0.87×** |
| Warmed | Typed 星泉（型別） | 45.08 | 43.03 | 0.96× | 0.96× |

Scores are C# = 100, the same method as earlier rounds: the benchmark repository's `run_benchmarks.py --profile large`,
5 measured processes and 1 discarded warm-up process per case, seed 20261010. Untyped AquariusLang is 2.9× faster in
standard mode and 2.8× faster warmed, and it now beats Node.js in standard mode.

Typed AquariusLang is unchanged within noise. The few typed cases that look slower (quicksort standard, binary trees
warmed) move by about as much as C# does between the two runs (binary trees C# 17.5 → 20.8 ms standard, collatz 16.9 →
18.1 ms). An interleaved A/B of the baseline and the final build (6 alternating processes each) showed no systematic
difference: typed quicksort 4.1–5.7 ms vs 4.3–4.6 ms, typed binary trees 32–54 ms vs 34–51 ms.

All outputs of every warm-up and measured sample, in every mode and language, matched the Python reference answers.

## Per-item status

| # | Item | Status | Commit | Notes |
| --- | --- | --- | --- | --- |
| 1 | Real hash tables | **Done** | `7dfae9c` | Pairs stay dense and in insertion order, so Keys, Values, Entries, iteration, printing, threads and native libraries are unchanged. Above 8 pairs an open-addressing index is built lazily over pairs appended since the last lookup, so host appends and truncations keep working. Text hashes are cached in the block header. Deletes backward-shift their index slot and renumber later positions. Pairs must stay dense for SDK readers, so a delete still shifts pairs (O(n)) instead of leaving a tombstone. |
| 11 | Native array kernels | **Done** | `2311554` | 排序/Sort (introsort for ints, stable merge sort for other numbers and strings), 總和/Sum, 最小值/Min and 最大值/Max run in C. 映射/Map, 篩選/Filter, 歸約/Reduce and Sort with a comparison call back into Aquarius. The guest runtime cannot re-enter the scheduler from C, so these check their arguments in C and then continue in the guest prelude `runtime/guest/prelude.aqua`. The compiler adds the prelude only to programs that name these kernels. Docs, LANGUAGE.md and the LSP tables are updated. The VS Code grammar does not list module members. |
| 5a | Strings: amortized `s = s + t`, interned one-unit strings | **Done** | `6ee48b0` | Indexing, one-character slices and FromCode share one static text per UTF-16 unit. `s = s + t` and `s += t` on a promoted local extend the text in place while a tag in `Value.number` proves the slot is its only reference. Every other load of that slot clears the tag. Module-level and captured variables still copy. A shared-slack concatenation for those would need a new string representation in the host ABI. |
| 8 | Tail calls and a lighter native calling convention | **Tail calls done; calling convention not done** | `0fee9de` | `return_call` when a typed function returns a call's result directly. Checked on Wasmtime 48, Chromium and Android. A context-pointer calling convention was not done: the hot native functions pass 0–2 outer pointers, all in registers, so one context pointer would only add memory loads. |
| 2 | Type feedback, wider stencils, profile-guided AOT | **Partly done (portable form)** | `1157aa4`, `e5e3160`, `df291cc` | Instead of runtime feedback, the TypeChecker's static guesses were widened and serve every target: arrays, arithmetic, values copied from variables, array elements, parameters, function results, solved to a fixpoint. Regions and stencils now cover typed and untyped arrays, and JIT variants keep array guesses. Not done: tag-bitmask feedback through `rt_jitRequest`, bool/struct stencil variants, more than 3 variables, and `aqua run --profile` / `aqua build --profile`. With the wider guesses every benchmark loop already compiles ahead of time, so feedback would only help programs whose types cannot be seen in the source. |
| 4 | Array element kinds | **Done** | `1157aa4` | `Array.kind` is maintained on every write. Writes of another tag, host access and mixing library calls reset it. Make, Fill, literals and copies set it. `rt_arrayKind` checks the kind at entry and scans only when it is unknown. Regions speculate on these "loose" arrays. An out-of-range read leaves the region at that read, which starts a generic block, so the result stays null. |
| 6 | Inline caches for member access | **Done (struct fields)** | `db70145` | Generic `p.x` / `p.x = v` check a per-site cache stored in the member name constant's unused number (struct type id + field index). It needs no layout change and survives collections. Module members already go through the scope hash index with cached text hashes, so they got no separate cache. |
| 3 | Typed clones of untyped functions | **Done** | `e5e3160`, `df291cc` | An untyped function whose parameters all have guesses gets a native clone with an inferred result type. Its entry adapter accepts exactly those types (no widening, arrays checked by kind) and otherwise runs the generic body. A read past the end of a guessed array deoptimizes at that read. Clones call each other directly. Calls from regions to clones were not added, because the clone of the enclosing function already covers those loops. |
| 9 | Lazy deoptimization | **Not done (see round 2)** | | The machinery the other items needed already exists: regions leave at loop heads or at exact reads, and native functions deoptimize at any generic block. Re-executing an iteration from the loop head is only sound for iterations without effects, and no benchmark needs it. |
| 10 | Escape analysis / scalar replacement; generational GC | **Not done** | | In the benchmarks every allocated struct or array escapes into a tree or an array, so scalar replacement would not apply. A sticky-mark-bit generational collector needs a write barrier in the guest runtime, the generic emitter, the native tier and every host write path. That is too risky to add next to the changes above in one session. |
| 5b | Typed 字串 in the native tier | **Done in round 2** | | No benchmark uses strings in typed code. Left for a follow-up. |
| 7 | Packed typed arrays + SIMD | **Not done** | | Changes the array ABI (SDK accessors, layout version, every library that reads arrays), which is too risky here. Typed array loops already run at native speed. |
| 12 | Parallel.For / 平行 | **Not done** | | Needs a worker pool, shared memory on three hosts and a sequential fallback. The benchmark rules forbid worker threads, so it cannot show up in these numbers. |
| 13 | Bounds-check elimination and loop-invariant hoisting | **Not done (measured in round 2)** | | The benchmark index expressions (`a[i*n+k]`, `flags[j]` with `j += p`, `board[prev]`) cannot be proven in range without symbolic range analysis. Cranelift already hoists loop-invariant code. Not worth it without range analysis. |

## Benchmark results (ms, median of 5 processes; × = before ÷ after)

### Standard mode (cold, one timing per process)

| Benchmark | n | Untyped before | Untyped after | × | Typed before | Typed after | × | Node.js (before / after) | C# (before / after) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 遞迴費波那契 Fibonacci | 32 | 15.761 | 11.886 | 1.33× | 11.526 | 10.785 | 1.07× | 13.918 / 15.217 | 5.227 / 5.108 |
| 阿克曼 Ackermann | 8 | 47.903 | 9.599 | 4.99× | 10.088 | 9.844 | 1.02× | 7.767 / 7.152 | 2.137 / 2.173 |
| 篩法 Sieve | 200000 | 2.867 | 1.416 | 2.03× | 1.523 | 1.539 | 0.99× | 5.772 / 5.734 | 0.747 / 0.758 |
| 快速排序 Quicksort | 30000 | 10.168 | 5.948 | 1.71× | 4.350 | 5.064 | 0.86× | 5.991 / 6.498 | 2.090 / 2.636 |
| 矩陣乘法 Matrix | 150 | 52.764 | 5.517 | 9.56× | 4.505 | 4.345 | 1.04× | 8.191 / 9.440 | 1.983 / 2.007 |
| 曼德布洛特 Mandelbrot | 300 | 3.975 | 4.137 | 0.96× | 4.027 | 4.194 | 0.96× | 5.044 / 5.173 | 3.183 / 3.867 |
| N 皇后 N-Queens | 10 | 25.420 | 5.011 | 5.07× | 4.919 | 4.896 | 1.00× | 4.337 / 4.415 | 3.112 / 3.186 |
| 二元樹 Binary trees | 16 | 132.506 | 62.085 | 2.13× | 50.808 | 53.370 | 0.95× | 28.375 / 28.427 | 17.540 / 20.847 |
| 譜範數 Spectral norm | 250 | 71.893 | 7.021 | 10.24× | 5.823 | 5.932 | 0.98× | 9.009 / 9.193 | 2.974 / 3.003 |
| 考拉茲 Collatz | 100000 | 15.688 | 15.218 | 1.03× | 15.184 | 16.234 | 0.94× | 43.900 / 45.738 | 16.947 / 18.119 |

| Language | Score before | Score after | vs Node.js before | vs Node.js after |
| --- | ---: | ---: | ---: | ---: |
| Untyped 星泉 | 16.74 | 49.33 | 0.460× | 1.305× |
| Typed 星泉（型別） | 50.02 | 52.66 | 1.376× | 1.393× |
| Node.js | 36.35 | 37.80 | 1.000× | 1.000× |
| C# | 100.00 | 100.00 | 2.751× | 2.646× |
| Python | 2.58 | 2.64 | 0.071× | 0.070× |

### Warmed mode (5 in-process warm-ups, median of 5 timings)

| Benchmark | n | Untyped before | Untyped after | × | Typed before | Typed after | × | Node.js (before / after) | C# (before / after) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 遞迴費波那契 Fibonacci | 32 | 16.557 | 11.908 | 1.39× | 11.309 | 11.907 | 0.95× | 14.758 / 15.451 | 5.153 / 5.147 |
| 阿克曼 Ackermann | 8 | 49.255 | 9.272 | 5.31× | 9.728 | 9.814 | 0.99× | 7.306 / 7.319 | 2.066 / 1.932 |
| 篩法 Sieve | 200000 | 2.713 | 1.235 | 2.20× | 1.181 | 1.297 | 0.91× | 2.455 / 2.316 | 0.365 / 0.386 |
| 快速排序 Quicksort | 30000 | 9.681 | 6.492 | 1.49× | 4.178 | 4.431 | 0.94× | 2.851 / 2.981 | 1.511 / 1.511 |
| 矩陣乘法 Matrix | 150 | 51.643 | 6.113 | 8.45× | 4.677 | 4.785 | 0.98× | 3.070 / 3.732 | 1.742 / 1.780 |
| 曼德布洛特 Mandelbrot | 300 | 3.869 | 3.936 | 0.98× | 3.863 | 4.115 | 0.94× | 3.052 / 3.328 | 3.082 / 3.085 |
| N 皇后 N-Queens | 10 | 25.556 | 5.120 | 4.99× | 4.950 | 5.060 | 0.98× | 3.774 / 3.994 | 2.844 / 2.819 |
| 二元樹 Binary trees | 16 | 137.905 | 70.509 | 1.96× | 36.884 | 43.352 | 0.85× | 13.937 / 15.968 | 17.401 / 19.287 |
| 譜範數 Spectral norm | 250 | 76.199 | 6.246 | 12.20× | 6.119 | 6.336 | 0.97× | 7.248 / 7.155 | 2.150 / 2.126 |
| 考拉茲 Collatz | 100000 | 15.750 | 16.032 | 0.98× | 15.270 | 15.657 | 0.98× | 42.432 / 45.112 | 17.273 / 17.001 |

| Language | Score before | Score after | vs Node.js before | vs Node.js after |
| --- | ---: | ---: | ---: | ---: |
| Untyped 星泉 | 14.14 | 38.99 | 0.302× | 0.874× |
| Typed 星泉（型別） | 45.08 | 43.03 | 0.963× | 0.964× |
| Node.js | 46.83 | 44.63 | 1.000× | 1.000× |
| C# | 100.00 | 100.00 | 2.136× | 2.241× |
| Python | 2.17 | 1.75 | 0.046× | 0.039× |

Raw data: `AquariusLang_PerformanceBenchmark/results/perf-roadmap-before/20261028-093428-316173/` and
`.../perf-roadmap-after/20261028-112703-641684/` (results.json, samples.csv, summary.csv, README.md). The baseline is
`main` at `fb80753`, built and frozen in a folder beside the repository (`../aqua-perf-baseline`); the final build is
frozen the same way (`../aqua-perf-after`). Both are full layouts with the compiler, libraries, web glue and the
windows-x64 runtime pack.

## Micro-benchmarks (`examples/perf_micro`, median of 5 runs, untyped code)

| Program | What it does | Before (ms) | After (ms) | × |
| --- | --- | ---: | ---: | ---: |
| hash_int | 100k integer keys: insert, then look every key up | 38367.3 | 15.9 | 2406× |
| hash_string | 100k string keys: insert, then look up | 22505.8 | 17.4 | 1297× |
| string_concat_local | `s = s + "ab"` 100k times on a local variable | 898.5 | 2.4 | 374× |
| string_concat | the same on a module-level variable (copying `+`, unchanged) | 1005.2 | 1009–1156 | ≈1× |
| string_index | `s[i] == "c"` over 100k characters × 10 | 61.8 | 53.1 | 1.16× |
| sort_aqua | quicksort of 1M integers written in Aquarius | 298.9 | 113.8 | 2.63× |
| sort_builtin | `陣列.排序` of the same 1M integers | — (no builtin) | 32.1 | 9.3× vs sort_aqua before |
| struct_field | `p.x = p.x + 1; p.y = p.y + p.x % 3` × 2M | 182.7 | 62.3 | 2.93× |
| helper_calls | untyped helpers `mix(total, i)` → `add(a, b)` × 2M | 171.0 | 47.5 | 3.60× |
| array_sum | sum of a 1M-element untyped array × 10 | 55.9 | 9.4 | 5.95× |

Interleaved re-runs of `string_concat` gave 1002–1149 ms (before) and 1009–1040 ms (after): unchanged.

## Tests

| Where | Result |
| --- | --- |
| Windows, `dotnet test compiler/AquariusTests` (final) | **778 passed**, 0 failed, 1 skipped (Linux-only) |
| Linux (WSL Ubuntu 22.04), `scripts/test-linux.sh`: runtime pack built with the pinned LLVM, then the full suite | **761 passed**, 0 failed, 18 skipped |
| Library tests, `scripts/test-libraries.ps1` (windows-x64 + web) | **68 passed**, 0 failed, 38 skipped (cases that need a device) |
| Library tests, `-Devices -Targets windows-x64` | 49 passed, 2 failed. `network/web_client` needs `tests/servers/peer.aqua` running; with it started, web_client passes on windows-x64 and web. `onnx/pose` needs model files that are not on this machine. |
| Library tests, `-Devices -Targets android-x86_64` (emulator, API 37) | 34 passed, 2 failed (the same two environmental cases), 17 skipped |
| Web (Chromium, `scripts/web-run.mjs`) | The untyped matrix, quicksort, sieve, N-queens, Fibonacci, Ackermann, spectral norm, binary trees and collatz programs print the same results as desktop. So does a check program covering tail calls, string appends, indexed hashes, kernels and string indexing. |
| Android emulator (`scripts/android-run.ps1`) | The untyped matrix, quicksort, Ackermann, spectral norm, binary trees and N-queens programs and the check program print the same results as desktop. |

New tests:
- `CloneTest`, `LooseArrayTest`, `InlineCacheTest` and `StringAppendTest`, all differential against the unoptimized compiler, with tiny quanta and with the JIT on and off.
- Tail-call cases in `NativeFunctionTest`.
- Kernel and callback cases in `ArrayLibraryTest` and `tests/array-library.json`.
- Indexed-hash cases in `tests/hash-library.json`.

Codegen goldens were regenerated after each intended emitter or guest-runtime change.

## Round 2: items 13, 5b and 9, then a final rerun

| # | Item | Status | Notes |
| --- | --- | --- | --- |
| 13 | Bounds-check elimination and loop-invariant hoisting | **Investigated, not implemented** | Ceiling measured by deleting every native read and write bounds check (unsafe, experiment only). Typed matrix gets ~20% faster (4.5 → 3.6 ms warmed), spectral norm and sieve ~5%, N-queens and quicksort not at all. The matrix gain needs loop versioning with affine-index analysis of computed indices (`a[i*n+k]`): Cranelift does not unswitch loops, so the native emitter would have to emit each innermost loop twice. Cranelift already hoists loop-invariant pure code. Left for a later round. |
| 5b | Typed 字串 in the native tier | **Done** (`bbada2a`) | `字串` values are `Text*` in i32 locals. `長度` and code-unit reads run in place. Equality, `+` and one-unit results call small runtime helpers. `s = s + t` on a string slot appends in place: the slot's ownership mark is the pointer's low bit, and any other read or frame write drops it. A read past the end deoptimizes at that read, so the generic code gives null. Native functions that allocate (strings, array literals, structs, directly or through callees) now poll for pending collections at loop heads. Clones accept string guesses. Typed palindrome micro: 33.3 → 2.0 ms. **Bug fix found on the way:** typed functions and regions that called `長度` / `len` or a conversion always fell back to the generic body at run time, because the entry check expected the core global in the closure's scope chain. It now accepts the builtin when no binding shadows it. |
| 9 | Lazy deoptimization | **Investigated, not implemented** | Ceiling measured by deleting the native loop-head checkpoints and their deoptimization sites entirely, which is more than deoptimizing back to the loop head could save. Typed matrix moves 0–10% (within noise), mandelbrot and collatz 0%, quicksort ~8%, and part of that is the fuel accounting that must stay. The warmed gaps to Node.js in these loops are not caused by values kept live for deoptimization. |

Final benchmark: the same harness and settings (`--profile large`, 5 + 1 processes, seed 20261010) on the final build
(`../aqua-perf-final`). Every sample matched the Python reference.

### Standard mode — main baseline → final

| Benchmark | n | Untyped before | Untyped final | × | Typed before | Typed final | × | Node.js (before / final) | C# (before / final) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 遞迴費波那契 Fibonacci | 32 | 15.761 | 10.994 | 1.43× | 11.526 | 11.499 | 1.00× | 13.918 / 14.589 | 5.227 / 5.146 |
| 阿克曼 Ackermann | 8 | 47.903 | 9.637 | 4.97× | 10.088 | 9.830 | 1.03× | 7.767 / 7.678 | 2.137 / 2.324 |
| 篩法 Sieve | 200000 | 2.867 | 1.419 | 2.02× | 1.523 | 1.642 | 0.93× | 5.772 / 5.742 | 0.747 / 0.781 |
| 快速排序 Quicksort | 30000 | 10.168 | 5.984 | 1.70× | 4.350 | 4.911 | 0.89× | 5.991 / 5.992 | 2.090 / 2.087 |
| 矩陣乘法 Matrix | 150 | 52.764 | 5.749 | 9.18× | 4.505 | 4.253 | 1.06× | 8.191 / 8.226 | 1.983 / 1.975 |
| 曼德布洛特 Mandelbrot | 300 | 3.975 | 4.151 | 0.96× | 4.027 | 4.103 | 0.98× | 5.044 / 4.969 | 3.183 / 3.286 |
| N 皇后 N-Queens | 10 | 25.420 | 5.029 | 5.06× | 4.919 | 4.933 | 1.00× | 4.337 / 4.412 | 3.112 / 3.154 |
| 二元樹 Binary trees | 16 | 132.506 | 57.491 | 2.30× | 50.808 | 48.854 | 1.04× | 28.375 / 28.217 | 17.540 / 16.560 |
| 譜範數 Spectral norm | 250 | 71.893 | 5.963 | 12.06× | 5.823 | 5.855 | 0.99× | 9.009 / 9.372 | 2.974 / 3.164 |
| 考拉茲 Collatz | 100000 | 15.688 | 15.621 | 1.00× | 15.184 | 15.663 | 0.97× | 43.900 / 45.240 | 16.947 / 17.563 |

| Language | Score before | Score final | vs Node.js before | vs Node.js final |
| --- | ---: | ---: | ---: | ---: |
| Untyped 星泉 | 16.74 | 47.90 | 0.460× | 1.306× |
| Typed 星泉（型別） | 50.02 | 50.34 | 1.376× | 1.372× |
| Node.js | 36.35 | 36.69 | 1.000× | 1.000× |
| C# | 100.00 | 100.00 | 2.751× | 2.726× |
| Python | 2.58 | 2.68 | 0.071× | 0.073× |

### Warmed mode — main baseline → final

| Benchmark | n | Untyped before | Untyped final | × | Typed before | Typed final | × | Node.js (before / final) | C# (before / final) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 遞迴費波那契 Fibonacci | 32 | 16.557 | 11.354 | 1.46× | 11.309 | 11.387 | 0.99× | 14.758 / 14.701 | 5.153 / 5.097 |
| 阿克曼 Ackermann | 8 | 49.255 | 9.533 | 5.17× | 9.728 | 9.578 | 1.02× | 7.306 / 7.206 | 2.066 / 2.015 |
| 篩法 Sieve | 200000 | 2.713 | 1.203 | 2.25× | 1.181 | 1.393 | 0.85× | 2.455 / 2.473 | 0.365 / 0.382 |
| 快速排序 Quicksort | 30000 | 9.681 | 6.128 | 1.58× | 4.178 | 4.559 | 0.92× | 2.851 / 2.779 | 1.511 / 1.545 |
| 矩陣乘法 Matrix | 150 | 51.643 | 6.114 | 8.45× | 4.677 | 4.672 | 1.00× | 3.070 / 3.246 | 1.742 / 1.742 |
| 曼德布洛特 Mandelbrot | 300 | 3.869 | 3.906 | 0.99× | 3.863 | 3.927 | 0.98× | 3.052 / 3.163 | 3.082 / 3.141 |
| N 皇后 N-Queens | 10 | 25.556 | 5.301 | 4.82× | 4.950 | 5.043 | 0.98× | 3.774 / 3.825 | 2.844 / 2.883 |
| 二元樹 Binary trees | 16 | 137.905 | 57.000 | 2.42× | 36.884 | 40.694 | 0.91× | 13.937 / 12.724 | 17.401 / 16.767 |
| 譜範數 Spectral norm | 250 | 76.199 | 6.090 | 12.51× | 6.119 | 5.719 | 1.07× | 7.248 / 6.893 | 2.150 / 2.079 |
| 考拉茲 Collatz | 100000 | 15.750 | 15.627 | 1.01× | 15.270 | 15.585 | 0.98× | 42.432 / 43.356 | 17.273 / 17.402 |

| Language | Score before | Score final | vs Node.js before | vs Node.js final |
| --- | ---: | ---: | ---: | ---: |
| Untyped 星泉 | 14.14 | 40.03 | 0.302× | 0.851× |
| Typed 星泉（型別） | 45.08 | 43.62 | 0.963× | 0.927× |
| Node.js | 46.83 | 47.06 | 1.000× | 1.000× |
| C# | 100.00 | 100.00 | 2.136× | 2.125× |
| Python | 2.17 | 2.18 | 0.046× | 0.046× |


Typed results stay within run-to-run noise of `main`. Typed sieve (warmed) reads 1.18 → 1.39 ms, but interleaved runs of
the two builds go both ways (200k: 1.16–1.53 vs 1.20–1.80 ms; 5M: 71–121 vs 62–137 ms); it is memory-bound. Typed binary
trees and quicksort moved by about as much as C# did between runs, as in round 1.

Micro-benchmarks on the final build (median of 5, ms), compared with `main`:

| Program | main | final |
| --- | ---: | ---: |
| hash_int | 38367.3 | 15.5 |
| hash_string | 22505.8 | 15.5 |
| string_concat_local | 898.5 | 0.64 (the untyped helper is now a clone, so it appends natively) |
| string_concat (module-level) | 1005.2 | 919.3 |
| string_index | 61.8 | 46.1 |
| sort_aqua / sort_builtin | 298.9 / — | 102.2 / 31.8 |
| struct_field | 182.7 | 53.4 |
| helper_calls | 171.0 | 47.1 |
| array_sum | 55.9 | 8.6 |
| typed_strings (new) | 34.2 | 2.0 |

Tests after round 2: Windows `dotnet test` **785 passed**, 0 failed, 1 skipped. New tests: `NativeStringTest` (13
differential programs, native-tier acceptance, collections in string loops under tiny quanta and with the JIT off).

## Language server and VS Code extension 0.1.3

The language server (`compiler/AquariusLanguageServer`) and the extension (`editors/vscode`) now cover the current
language. Both are version **0.1.3** (server `serverInfo`, `package.json`, `package-lock.json`, the VSIX name, the READMEs).
`build-release.ps1` reads the version from `package.json`, so it needed no change.

- **Portable modules.** Every function of 陣列/Array (including the new kernels 排序/Sort, 總和/Sum, 最小值/Min,
  最大值/Max, 映射/Map, 篩選/Filter, 歸約/Reduce), 雜湊/Hash, 字串/String and 執行緒/Thread has a bilingual signature and
  description in hover and completion (`陣列.排序(陣列, 比較?) / Array.Sort(array, compare?) — …`). Before, hover showed
  only the two names. A new test reads the guest runtime's name and arity tables and checks every parameter list
  against them (optional parameters end in `?`).
- **Signature help** (new: `textDocument/signatureHelp`, triggered by `(` and `,`). It covers declared functions with
  their annotations, struct constructions (the fields with their types), portable and native module members in the
  language the call is written in, core globals and conversions. Nested calls and the active argument are handled;
  nothing is shown inside strings or comments.
- **Core globals.** 整數/int, 浮點數/float and 雙精度/double are described as conversions where they are called (and
  stay type names after a colon). 區域填色/floodFill now documents its six parameters.
- **TextMate grammar.** It now highlights:
  - every core global (型別, 目前時間, 目前日期, 是Android, 是網頁, 區域填色 and their English names);
  - conversions, as calls only;
  - the portable modules with their members;
  - struct types in annotated declarations (`變數 根: 節點`) and function results (`): 節點`).
- **Already covered and checked.** Structs, the 陣列/array annotation, typed annotations and their compile-time
  diagnostics, document symbols for structs and fields, and module completion.
- **Tests.**
  - LanguageServerTest gained four cases: rich kernel and module docs plus conversions, arities against the runtime
    tables, signature help, and capabilities/version. 34 language-server tests now pass.
  - `editors/vscode/test/manifest.test.js` gained a grammar test for the new patterns (5/5 pass).
  - `npm run check` and `npm run package` built `aquariuslang-tw-0.1.3.vsix`, and the release script's language-server
    initialize check passes on the bundled server.
- **Full suite after this step:** 782 passed, 0 failed, 1 skipped.

## Notes for reviewers

- Layout: `Hash` gained `index` and `indexed` (same 32-byte allocation class). In `Array`, `dirty` became a `uint16_t` next to the new `uint16_t kind`, so every existing offset is unchanged; `layout.def` lists both. Hosts and SDK readers see no change: pairs stay dense and in order, and `aqua_array_values` / `aqua_array_items` reset the element kind.
- `runtime/guest/prelude.aqua` is AquariusLang code shipped inside the guest runtime and embedded in the compiler. The architecture rule for `.aqua` locations allows exactly that file.
- The guest runtime has a 512 KB static table of one-unit texts. Static data still ends near 2.4 MB, below the 4 MB program address.
- The runtime JIT's code cache now has 1024 slots for JIT modules. Before, several loops of one program kept evicting each other from the 32 direct-mapped slots and recompiling on every run.
