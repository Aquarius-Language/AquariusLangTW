# Performance investigation — 2026-10-18

The benchmark was selecting the **2026-10-15 installed compiler through PATH**,
not the current `PlatformSpecificUpgrade` source. Both saved 2026-10-17 reports
identify that same old executable and the same Core/Desktop assembly hashes.
The current branch starts at `db6ff61` and was clean before this work.

The installed executable was:

`C:\OfficialProjects\AquariusLangTW\dist\releases\AquariusCompiler-win-x64-20261015-210053-89186526\aqua\aqua.exe`

Fresh measurements show that the existing branch's optimizations already work.
Alternating installed-release and final artifacts on the same final desktop host
gives **6.26x** equal-weight geometric-mean speedup. This includes the
optimizations that were already on the branch. The additional portable compiler
changes in this work provide **1.062x (6.2% faster)**
beyond the untouched branch, in a separate controlled artifact comparison.

## Changes

* **Portable core:** cache positive dynamic binding addresses in Wasm locals across
  basic blocks and loop backedges. Read values from the binding on every access.
  Invalidate on helpers/calls, including scope changes and callbacks; start fresh
  on resumption. Exclude unreachable accesses and already promoted bindings.
  This adds no platform imports, host APIs or artifact-layout changes.
* **Desktop:** cache the memory base and bounds between guest transitions, avoiding
  native queries per primitive value read. Refresh before callbacks and after
  every return, including traps and reentry; retain bounds/disposal checks.
* **Browser/shared JS host:** reuse DataViews while the memory buffer is unchanged.
  Memory growth replaces the cached view. This also benefits Android's JS host.
* **Android JNI:** cache immutable export signatures; keep up to eight arguments
  and results on the stack. Larger signatures retain dynamic storage. Rebuilt
  both ARM64 and x86-64 native libraries and verified the runtime on the emulator.
* Published the updated desktop runtime pack and self-contained compiler, so
  exported applications and fresh compiler runs use the optimized adapter.

The existing core scalar tier only accepts closed scalar functions; arrays,
captured variables and general loops still need generic execution. Repeated
binding resolution in that generic path was a remaining cost. The desktop
adapter's previous typed invokers and cached memory export did not eliminate
native pointer/bounds queries on every primitive read.

## Controlled compiler comparison

Before: artifacts compiled with the untouched `db6ff61` compiler. After: final
artifacts. Both execute on the identical final host, alternating order, with five
in-process warmups and fifteen measured executions per variant. Sources, inputs,
algorithms and reference checksums are unchanged. No tests/builds ran concurrently.

| Workload | Branch median ms | Final median ms | Speedup |
| --- | ---: | ---: | ---: |
| fibonacci | 2.8462 | 2.8643 | 0.994x |
| ackermann | 0.6634 | 0.6572 | 1.009x |
| sieve | 0.1525 | 0.1447 | 1.054x |
| quicksort | 0.6190 | 0.6169 | 1.003x |
| matrix | 1.9595 | 1.7166 | 1.142x |
| mandelbrot | 1.9425 | 1.9293 | 1.007x |
| nqueens | 1.4756 | 1.2197 | 1.210x |
| binary_trees | 6.4296 | 6.1530 | 1.045x |
| spectral_norm | 4.8417 | 5.0184 | 0.965x |
| collatz | 5.4543 | 4.4535 | 1.225x |

The geometric mean is 1.062x. Spectral norm measured about
3.5% slower in this paired pass;
Fibonacci was effectively unchanged. No claim is made that every workload improves.
The final process-based run showed improvements in all ten workloads, but its
separate-process timing is noisier; the controlled result is the conservative
measure of the compiler change.

The separate installed-artifact comparison ranges from
2.59x to
23.99x per workload.
Full samples and artifact/source hashes are in the JSON evidence.

## Platform hot paths

Reading 1,000,000 i32 fields from 16-byte guest value cells through the actual
desktop `IWasmInstance.Memory` interface took 11.5534 ms
before and 1.0927 ms after: **10.57x**.
The JavaScript view comparison took 54.7510 ms versus
13.8039 ms: **3.97x**.
These isolate embedding hot paths; they are not whole-application speedups.
Android correctness was measured on the emulator; no Android timing claim is made.

## Final original benchmark

After all implementation changes and regression checks, ran the original sibling
benchmark runner with an explicit final executable, nine repetitions and two
warmups. All **30 cases / 330 executions** passed reference validation.
All 30 source hashes match both the original report and fresh branch baseline.
Its ten Aquarius medians have a geometric-mean 1.111x speedup over
the fresh branch process-based baseline (which used five samples and one warmup).

Final normalized scores: Aquarius **100.00**, Node.js
**90.95**, Python **14.47**.
These scores apply only to this suite, input sizes and timing boundary. Timing
includes initialization and computation, and excludes startup, compilation and
output. Each process-based sample starts a new process; an earlier warmup process
does not warm later sample processes. Paired diagnostics additionally measure
reused, warmed sessions.

```powershell
python C:\OfficialProjects\AquariusLang_PerformanceBenchmark\run_benchmarks.py `
  --aquarius C:\OfficialProjects\AquariusLangTW\dist\performance-20261018\aqua.exe `
  --repetitions 9 --warmups 2
```

The benchmark repository's README and latest JSON/CSV reports were updated by
its original runner. PATH still points to the older installed release; keep the
explicit `--aquarius` argument for future runs, or update your installation.

## Verification

* Entire Release solution builds with zero errors; existing warnings remain.
* Desktop: **1,803 passed**, zero skipped/failed, with native OpenGL, WebGPU and
  application tests enabled and test collections serialized.
* Browser/WebAssembly: **418 passed**; language server: **23 passed**.
* Android host tests: **98 passed**; native builds passed for ARM64 and x86-64.
* Real emulator: runtime contract, threading verification, reentrant callbacks,
  i64, traps, memory growth, and repeated nine-parameter/nine-result calls passed.
  Temporary test applications were removed.
* Added differential binding-cache tests covering tiny scheduling quanta,
  shadowing, mutations, scope reallocation and unreachable instructions; desktop
  memory tests cover callbacks, reentry, growth, traps, bounds and disposal.
* Final standard benchmark: 330 checksum-validated executions. Two paired
  comparisons: 800 checksum-validated executions, including warmups.

Raw reports: [measurements](../benchmarks/execution/measurements/20261018/).
Reproduction tools: [execution diagnostics](../benchmarks/execution/README.md).
Source and runtime fingerprints: [summary.json](../benchmarks/execution/measurements/20261018/summary.json).
