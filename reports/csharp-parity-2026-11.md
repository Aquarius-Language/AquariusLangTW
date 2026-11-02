# C# parity round — 2026-11

Branch `perf/csharp-parity` (AquariusLangTW and AquariusLang_PerformanceBenchmark), started from `main` at `036e9b4`,
not merged. Every change follows CLAUDE.md: steps 1–7 and 10 are portable (compiler or guest runtime, the same `.wasm`
gains on every target); step 11 is a desktop engine/platform-layer option (rule 11) with Wasmtime kept for `aqua run` and
every other executable.

## Result

| Mode | Language | Score before | After steps 1–11 | After the preemption fix (final) | × C# time before → final |
| --- | --- | ---: | ---: | ---: | ---: |
| Standard | Typed 星泉 (Wasmtime) | 57.66 | 68.42 | **66.50** | 1.73× → **1.50×** |
| Warmed | Typed 星泉 (Wasmtime) | 50.45 | 59.28 | **56.10** | 1.98× → **1.78×** |
| Standard | Untyped 星泉 (Wasmtime) | 45.98 | 51.60 | 49.92 | 2.17× → 2.00× |
| Warmed | Untyped 星泉 (Wasmtime) | 40.72 | 43.09 | 43.32 | 2.46× → 2.31× |
| Standard | Typed 星泉, `--optimize` (wasm2c AOT)¹ | — | **≈89** | — | **≈1.12×** |
| Warmed | Typed 星泉, `--optimize` (wasm2c AOT)¹ | — | **≈75** | — | **≈1.34×** |
| Standard | Node.js | 34.56 | 36.42 | | |
| Warmed | Node.js | 45.68 | 44.51 | | |

Scores are C# = 100 (geometric mean over the ten benchmarks), `run_benchmarks.py --profile large`, 5 measured processes
plus 1 discarded per case, seed 20261010. Before: `results/csharp-parity-baseline/20261102-093609-361106` (main, frozen in
`../aqua-perf-baseline`). After: `results/csharp-parity-final/20261102-115538-646607` (this branch, frozen in
`../aqua-perf-final`). With the preemption fix (commit `0108d29`, "Native preemption" below):
`results/csharp-parity-final-preempt/20261102-125259-431456` (frozen in `../aqua-perf-final-preempt`); the benchmark README
shows this run. Every sample of every run matched the Python reference answers. The per-benchmark tables below are the
run before the fix; the fix changes Ackermann and binary trees (see its section).

¹ The harness only runs `aqua run`, so AOT executables were timed in the bake-off (below) and divided by the final run's
C# medians; the bake-off ran on the same machine an hour earlier. Against Node.js in the final run: typed `--optimize`
is 2.45× (standard) and 1.67× (warmed) faster; typed Wasmtime 1.78× / 1.17×.

The typed benchmark scripts now declare `常數 benchmark_n` (step 2), the counterpart of C#'s `static readonly int`. With
the old `變數` declaration, matrix and spectral norm are about 3% slower; the other benchmarks do not change.

Machine: i9-13900H, Windows 11, AC power. Absolute timings drifted upward during the day by up to 2× on binary trees,
while a cloud-sync daemon was busy (`cloud-drive-daemon`, about 2 h of CPU time). So every per-step number below comes from an interleaved A/B run (both
builds alternate process by process), not from comparing runs at different times.

### Per benchmark (typed, ms, median)

| Benchmark | Std before | Std after | Std untyped | Std C# | Std Node | ×C# before | ×C# after |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| fibonacci | 11.293 | 10.120 | 10.172 | 5.263 | 14.293 | 2.18× | 1.92× |
| ackermann | 10.211 | 6.891 | 6.738 | 2.157 | 7.126 | 4.78× | 3.19× |
| sieve | 0.824 | 0.752 | 1.415 | 0.750 | 5.565 | 1.08× | 1.00× |
| quicksort | 2.226 | 2.251 | 3.976 | 2.156 | 6.542 | 1.07× | 1.04× |
| matrix | 4.414 | 2.140 | 5.953 | 1.970 | 8.472 | 2.05× | 1.09× |
| mandelbrot | 4.434 | 4.176 | 4.009 | 3.126 | 4.879 | 1.30× | 1.34× |
| nqueens | 4.446 | 4.065 | 5.630 | 3.123 | 4.319 | 1.38× | 1.30× |
| binary_trees | 51.283 | 39.874 | 47.046 | 14.958 | 28.931 | 3.33× | 2.67× |
| spectral_norm | 5.763 | 4.011 | 5.345 | 3.029 | 7.902 | 1.80× | 1.32× |
| collatz | 16.301 | 17.792 | 15.740 | 17.139 | 44.157 | 0.92× | 1.04× |

| Benchmark | Warm before | Warm after | Warm untyped | Warm C# | Warm Node | ×C# before | ×C# after |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| fibonacci | 11.069 | 10.214 | 10.072 | 5.052 | 14.414 | 2.16× | 2.02× |
| ackermann | 9.493 | 6.597 | 6.564 | 2.015 | 7.177 | 4.68× | 3.27× |
| sieve | 0.859 | 0.782 | 1.214 | 0.379 | 2.323 | 2.22× | 2.06× |
| quicksort | 2.268 | 2.288 | 4.250 | 1.448 | 2.835 | 1.45× | 1.58× |
| matrix | 4.318 | 2.174 | 5.951 | 1.750 | 4.567 | 2.41× | 1.24× |
| mandelbrot | 4.363 | 4.153 | 4.170 | 3.108 | 3.122 | 1.32× | 1.34× |
| nqueens | 4.415 | 4.142 | 5.686 | 2.721 | 3.731 | 1.50× | 1.52× |
| binary_trees | 38.270 | 26.244 | 51.985 | 14.258 | 13.500 | 2.38× | 1.84× |
| spectral_norm | 5.734 | 3.918 | 5.040 | 2.062 | 6.748 | 2.75× | 1.90× |
| collatz | 16.137 | 17.448 | 15.547 | 17.798 | 43.053 | 0.93× | 0.98× |

Collatz and quicksort warmed moved by about as much as the noise between processes; no step touched their loops
(collatz's inner loop is not a counted `k < n; k++` loop, and its interleaved A/B runs never showed a difference).

## Step 0: confirming the analysis

| Experiment | Result |
| --- | --- |
| a) Fuel slices: `AQUARIUS_FUEL_QUANTUM` 17 / 65536 / 2^20 / 2^30 | Ackermann 9.31 / 9.53 / 9.28 / 9.79 ms, Fibonacci 10.64 / 11.41 / 10.53 / 10.94 ms: **no effect**. The 1–2 ms hypothesis is refuted, but for an unexpected reason: native entry checkpoints test `budget == 0`, and the native budget is not saturated, so once a deep recursion's budget skips past 0 it never yields again (see "Native preemption" below). |
| b) `benchmark_n` replaced by the literal | matrix 4.310 → 4.127 ms (−4.2%), spectral norm 5.683 → 5.581 ms (−1.8%): the ceiling of step 2. |
| c) Ackermann's tail calls written as a loop | 9.400 → 8.377 ms (−10.9%). Step 1 then did better (−25%), because the compiled loop also keeps the depth and skips the call. |

## Steps 1–10

Times are warmed medians of interleaved A/B runs (5–11 processes each, large inputs), previous build → this step.

| # | Step | Commit | Before → after (ms) | Kept? | Why |
| --- | --- | --- | --- | --- | --- |
| 1 | Self tail calls as loops in the native tier | `5369792` | Ackermann 9.415 → 7.019 (−25%); Fibonacci, N-Queens unchanged | **Kept** | Arguments are assigned to the parameter slots, and the body restarts after the entry checkpoint's budget test and charge. This avoids `return_call`'s full epilogue and prologue. |
| 2 | `常數` / `const` (language, type checker, LSP, VS Code grammar, docs, tests); folding in native code; benchmark scripts use it | `b78c6a7`, bench `8da8fd5` | matrix 4.800 → 4.644, spectral norm 6.287 → 6.083 (−3%) | **Kept** | Folded only for a number or Boolean literal declared directly in a scope ahead of the function or loop, so a function called before the declaration still gets the generic error. |
| 3 | Collection polling only in loops that allocate (directly or via a callee) | `7aa0a62` | matrix 4.113 → 3.033 (−26%), sieve 0.941 → 0.708 (−25%), binary trees −5% | **Kept** | A new test allocates about 5 GiB only through a callee, and it fails without the callee rule. |
| 4 | Inlined leaves run no entry checkpoint | `00308be` | spectral norm 5.428 → 4.538 (−16%), untyped 5.792 → 4.994 | **Kept** | The weight is still charged in place; the next loop head yields. |
| 5 | Entry and loop-head tests as separate branches | `9bb9c54` | Fibonacci 10.313 → 9.619 (−7%), N-Queens 4.686 → 4.280 (−9%) | **Kept** | The entry still tests `budget == 0` (see "Native preemption"). |
| 6 | Strip-mined fuel, done as versioned loops: an innermost counted loop runs a copy without its head checkpoint when the budget covers every iteration | `dc1b8f9` | matrix 3.266 → 2.597 (−20%), spectral norm 4.691 → 4.419 (−6%), N-Queens 4.672 → 4.015 (−14%) | **Kept** | Exact: no yield can fall in the fast copy, and it charges `weight × (k − k0 + 1)`. Budgets and yield points equal the original's, so the quantum 1/3/17 tests pass unchanged. |
| 7 | Loop versioning for affine indices: one range proof before the loop, unchecked body, checked loop as fallback | `f8f1fa8` | matrix 2.597 → 2.353 (−9%), spectral norm 4.419 → 4.172 (−6%) | **Kept** | Sums, differences and products of `k`, constants and invariants are proven at both ends with i64 arithmetic, and every node must fit an int. Mutation-tested. |
| 8 | Smaller live state at deoptimization exits | — | not implemented | **Not done** | After steps 6–7, matrix's inner loop is 11 instructions with no spills (disassembly of the fast copy). In Fibonacci, the five values held across the calls (n, depth, vmctx, closure, first result) are all needed by the normal path; deoptimization adds none, so a side table would remove nothing. |
| 9 | Deoptimization by Wasm exceptions | — | ceiling: Fibonacci +3% (noise), Ackermann −4.5% | **Not done** | The ceiling was measured with an incorrect build that drops the post-call `0x80000000` test entirely. Wasmtime 48 supports the proposal (`wasm_exceptions`), but ≤5% does not justify try/catch at every call, a JIT encoder change and browser/Android validation. |
| 10a | Arrays of up to four elements are one heap block | `c8bb86f` | binary trees typed 67.9 → 56.6 (−17%), untyped 62.8 → 55.7 (−11%) | **Kept** | The inline storage has its own header that redirects marking to the owning block, so storage shared by a functional `push` survives the owner. The copy-on-write recount is kept. A new test fails if the redirect is removed. |
| 10b | Bump-pointer nursery, minor collections, generational collection with a write barrier | — | — | **Not done** | It touches the guest runtime, both emitters, the native tier and every host/SDK write path. That could not be done safely in this round, and binary trees remains the largest allocation gap (1.84× warmed). The old collector would stay selectable for differential tests. |

### Native preemption (found in step 0; middle ground applied, commit `0108d29`)

Native entry checkpoints tested `budget == 0`, but the native budget is a local that goes below 0 where the generic one
saturates at 0. So a recursion-only native function (Fibonacci, Ackermann, tree builders) whose budget skipped past 0
never yielded again until it returned to a loop head or generic code: a long typed recursion froze every other task, and
the UI on web and Android, and pending collections waited as long. Measured with a test program (an async ticker counting
while `fib(30)` runs) before the fix: the ticker did not advance on desktop (quanta 65536 and 2^20), in Chromium or on the
Android emulator.

The owner chose the middle ground: a function **without loops** tests `budget <= 0` at its entry and at its self
tail-call re-entry, and a function with loops keeps `budget == 0` (it yields at its next loop head, which tests `<= 0`).
After the fix the ticker advances during `fib(30)` and `ack(3, 8)` on desktop (both quanta), in Chromium and on the Android
emulator. `NativeFunctionTest.LongTypedRecursionYieldsToOtherTasks` checks this under the portable and the desktop quantum
with the generic results, and it fails if the fix is removed. Results never change: only where a yield falls changes, and
generic code yields there too.

Desktop cost, A/B against the build before the fix (large inputs, 7 processes, ms):

| Benchmark | Warmed before → after | Standard before → after |
| --- | --- | --- |
| Typed Ackermann | 6.698 → 7.329 (+9%) | 6.925 → 8.299 (+20%) |
| Typed Fibonacci | 10.725 → 9.967 (−7%, noise) | 10.183 → 10.243 (+1%) |
| Typed binary trees | 27.168 → 38.518 (+42%) | 45.182 → 55.318 (+22%) |
| Untyped Ackermann | 6.698 → 7.389 (+10%) | 6.937 → 7.697 (+11%) |
| Untyped Fibonacci | 9.928 → 10.661 (+7%, noise) | 10.236 → 10.102 (−1%) |
| Untyped binary trees | 48.235 → 38.947 (−19%, noise) | 52.904 → 50.545 (−5%) |

In these benchmarks every hot function is loop-free recursion, so the middle ground costs what the full fix cost. Typed
binary trees now collects during its deep tree build, which it never did before. Ackermann now deoptimizes its 2,000-frame
native stack at each slice's end. Composite scores (full run): typed 68.42 → 66.50 standard and 59.28 → 56.10 warmed.

## Step 11: desktop ahead-of-time bake-off

Both prototypes ran the same `.wasm` as every target, behind the existing `engine.h` interface (one import, the
`aqua_*` exports, memory, traps), with no runtime JIT (precompiled stencils were not built for either). Each was tuned:

- **A) LLVM AOT via wasm2c + the pinned Clang** (WABT 1.0.42, `-O2`). wasm-rt counts call depth in a thread-local, which
  made Fibonacci 20 ms instead of 12. It is turned off: native recursion is bounded by the native tier's depth limit, guest
  recursion runs on heap frames, and the launcher reserves a 64 MiB stack. WABT 1.0.42 also emits a split local
  declaration in the tail-call variant of some functions. A narrow, tested textual repair fixes it
  (`AheadOfTime.Repair`).
- **B) WAMR** (`wamrc` 2.4.5 `--opt-level=3 --enable-tail-call`, runtime 2.4.5 built from source with the pinned Clang).
  On Windows its loader cannot map AOT code below 2 GiB, so it needs the large code model (`--size-level=0`): every call
  becomes `movabs` + `call *%rax`. Its loader also prints warnings on standard output.

All 40 cases are the 10 benchmarks × typed/untyped × standard/warmed at large inputs, with 5 processes plus 1
warm-up each. Each column gives the geometric mean of the time relative to Wasmtime with this round's compiler:

| | wasm2c + Clang | WAMR |
| --- | ---: | ---: |
| Typed, warmed | **0.700** | 0.727 |
| Typed, standard | **0.727** | 0.756 |
| Untyped, warmed | **0.756** | 0.795 |
| Untyped, standard | **0.765** | 0.801 |
| Results equal to Wasmtime (40 benchmark cases) | 40/40 | 40/40 |
| Repository language cases (`tests/*.json`: 4 programs of all 68 valid cases, 20 error cases) | 24/24 | 24/24 (after removing the loader's warnings) |
| Standard-mode process wall time (startup + run), geometric mean | **lowest** (e.g. sieve 13 ms) | +5–15% |
| Build time per app (mean) | 4.8 s (wasm2c + Clang + link) | **3.5 s** (wamrc) |
| Robustness | one WABT codegen defect repaired; nonconforming stack check | large code model on Windows; stdout warnings |

Selected benchmarks (typed, warmed, ms): Wasmtime / wasm2c / WAMR — Fibonacci 9.90 / 11.60 / 18.67, Ackermann 6.70 / 3.66
/ 3.85, sieve 0.96 / 0.38 / 0.39, matrix 2.18 / 1.47 / 0.90, mandelbrot 4.24 / 3.17 / 3.90, spectral norm 4.36 / 2.42 /
2.44, collatz 17.24 / 11.37 / 11.54, binary trees 41.1 / 39.2 / 41.3. Both are slower than Wasmtime on Fibonacci: the native
tier returns two values (result, budget), which both translators handle less well than Cranelift. Untyped benchmarks lose the
runtime JIT in both, and still gain 20–25% overall.

**Winner: wasm2c + Clang.** It is fastest in all four speed categories (about 4–5% ahead of WAMR) and starts fastest.
WAMR builds faster. The compiler/package size was ignored, as asked. Raw data: the bake-off script and `bakeoff.json` are
kept with the session's notes (not committed). The commands are in the commit message of `3052a08`.

**aarch64 (Jetson Orin Nano, Ubuntu aarch64):**

- wasm2c's output is portable C. The pinned Clang cross-compiles it for `aarch64-linux-gnu`, and wasm-rt supports aarch64
  Linux with guard pages. What is missing is a `linux-arm64` target (toolchain file, sysroot pin, Wasmtime aarch64 pin for
  the regular launcher, platform layer is already POSIX).
- WAMR: `wamrc --target=aarch64` cross-compiles from x86-64, and the runtime supports aarch64 Linux. It needs the same new
  target.

Neither is blocked on aarch64.

**Wired up:** `aqua build --target windows|linux --optimize true` (`compiler/AquariusBuild/AheadOfTime.cs`, commit
`3052a08`):

- The pinned wasm2c (`scripts/toolchain.json` now pins WABT 1.0.42 for windows-x64 and linux-x64) and the pinned
  Clang/CMake/Ninja are installed on first use by `scripts/install-toolchain`, with SHA-256 checks.
- The build compiles the app into `app.aot.dll` / `app.aot.so` beside the executable, through the pack's `aot/` project.
- The executable is the pack's new `aquarius-aot` launcher (`runtime/platform/desktop/engine_aot.c`) with the `.wasm`
  embedded as usual. The engine checks the library's SHA-256 against the module.
- Tested on Windows and Linux (`ExecutableTargetTest.OptimizedExecutablesMatchTheRegularOnesAndCheckTheirLibrary`).
  Example: typed Ackermann 6.51 ms with the regular executable, 3.34 ms optimized. The build took 7.7 s, toolchain
  already installed.

The WAMR prototype engine was removed from the tree. Its findings are this section.

**Not done in step 11:** precompiling the runtime JIT's stencils into the AOT build. The planned design keeps `jit.c` the
only Wasm encoder:

1. A runtime mode dumps every stencil module exactly as `jit.c` assembles it.
2. Each dump is translated and linked into the app library.
3. `engine_install` returns the precompiled variant's table index, found by the module's bytes.

The engine's table and cross-instance import plumbing (stencil modules import the app's memory, table and globals) is
the remaining work. Untyped loops already covered by native regions are compiled ahead of time anyway.

## Tests

| Where | Result |
| --- | --- |
| Windows, `dotnet test compiler/AquariusTests` | **876 passed**, 0 failed, 1 skipped (Linux-only), including the preemption test; codegen goldens updated |
| Linux (WSL Ubuntu 22.04), `scripts/test-linux.sh` (runtime pack with the pinned LLVM, then the suite) | **857 passed**, 1 failed, 18 skipped. The failure was the `--optimize` test: the toolchain installer cannot `mv` into the Windows drive (`/mnt/c`) from WSL. Rerun with `AQUARIUS_NATIVE_BUILD` on the Linux file system, it **passes** (56 s, toolchain installed on first use). |
| Library tests, `scripts/test-libraries.ps1` (windows-x64 + web) | **81 passed**, 0 failed, 51 skipped (need a device or window), before and after the preemption fix |
| Library tests, `-Devices -Targets android-x86_64` (emulator, API 37) | **46 passed**, 2 failed, 18 skipped, before and after the preemption fix. The 2 failures are the same environmental ones as earlier rounds: `network/web_client` needs `tests/servers/peer.aqua` running, and `onnx/pose` needs model files that are not on this machine. |
| Web (Chromium, `scripts/web-run.mjs`) | All 20 benchmark programs (typed and untyped, standard inputs) print the same results as desktop. Binary trees at n=16 (the earlier large-heap crash) runs to the correct result. |
| Android emulator (`scripts/android-run.ps1`) | Typed Ackermann, matrix, spectral norm, N-Queens, binary trees and Fibonacci, plus untyped quicksort, binary trees and matrix: same results as desktop. |
| VS Code grammar (`node --test test/manifest.test.js`) | 6 passed |
| Benchmark harness (`python -m unittest discover -s tests`) | 26 run, OK (1 skipped) |
| Benchmark run (final) | every sample matched the reference answers |

New tests:

- `ConstantTest`: parsing, errors, folding, a differential test under quanta 1/3/17, and the uninitialized-read case.
- `LoopVersioningTest`: a differential test with out-of-range ends, negative coefficients, 32-bit wrapping, break,
  continue, inlined calls, outer bounds and quanta 1–2^20. Mutation-tested.
- `NativeFunctionTest`: self tail calls (conversions, arrays, append, errors, quanta 1/3/7/17/4096), and a loop that
  allocates only in a callee. Mutation-tested.
- `ArrayLibraryTest.SmallArraysShareTheirInlineStorageSafely`, mutation-tested.
- `LanguageServerTest.ConstantsAreDeclaredHoveredAndProtectedFromWrites`.
- `ExecutableTargetTest`: the optimized executable and the wasm2c repair.
- The VS Code grammar test.

Codegen goldens were regenerated for steps 5–7 and 10a.

## Left undone

- Step 8 (no remaining target found), step 9 (ceiling too small), step 10b (nursery and generational collection).
- Precompiled JIT stencils in AOT executables.
- `docs/agent-reference.md` / `AGENT_REFERENCE.md` lives on the unmerged branch `claude/project-thread-sn71yp` (commit
  `3938428`), not on `main`. Add `常數` / `const` there when it merges (LANGUAGE.md, the READMEs and the language
  server already cover it).
- A `linux-arm64` target for the Jetson.
