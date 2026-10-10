# Verified Wasm migration measurements

Date: October 10, 2026. Windows 11 x64, Intel i9-13900H, Balanced power plan.
Every benchmark process was restricted to logical CPU 0 (`0x1`). These results
measure the implemented runtime migration, not completion of the full backlog.

The equally weighted geometric-mean speedup against the preserved legacy VM is
**1.744x for warmed execution** and **1.128x for
precompiled-Wasm process execution**. Source-to-Wasm process execution is
**1.002x**, effectively unchanged overall. This is not an
all-workload speedup: binary trees and matrix still regress when warmed, and
several process measurements retain startup regressions.

## Workload medians

Milliseconds, smaller is faster. The warm speed column is legacy divided by Wasm.

| Workload | Legacy process | Wasm process | Legacy warm | Wasm warm | Warm speed |
| --- | ---: | ---: | ---: | ---: | ---: |
| ackermann | 199.803 | 196.274 | 71.196 | 41.633 | 1.710x |
| binary_trees | 184.069 | 230.149 | 59.132 | 71.322 | 0.829x |
| collatz | 637.138 | 345.735 | 384.048 | 190.559 | 2.015x |
| fibonacci | 499.709 | 350.534 | 241.845 | 196.666 | 1.230x |
| mandelbrot | 290.884 | 188.670 | 129.339 | 36.479 | 3.546x |
| matrix | 210.960 | 225.829 | 65.470 | 72.837 | 0.899x |
| nqueens | 177.003 | 167.773 | 40.670 | 15.358 | 2.648x |
| quicksort | 149.656 | 163.112 | 19.843 | 10.552 | 1.881x |
| sieve | 160.164 | 168.061 | 43.474 | 12.671 | 3.431x |
| spectral_norm | 342.492 | 290.966 | 169.502 | 124.392 | 1.363x |

Process runs use one discarded warmup and five samples, randomized across legacy
source, Wasm source and precompiled Wasm. All 198 executions passed independent
reference validation. Wasm generation is outside precompiled timing. Engine
compilation is inside process timing, but the warmup may populate the enabled
Wasmtime compilation cache; these are not empty-cache first-launch timings.

Warm runs reuse a compiled program and runtime, with five warmups and five samples
per workload. Both profilers use matching minimal capabilities and .NET 8.0.31.
Parsing and engine compilation are excluded. Every output passed validation.
Instrumentation runs separately: each numeric workload makes one native call for
its final print, with one or two capability lookups. Computation and portable
append/length execute inside Wasm.

Guest heap is a high-water mark; managed allocated bytes are total allocation,
neither is peak or retained process memory. The fresh Sieve instrumented run used
837,328 guest heap bytes after
sharing array storage, compared with the near-limit intermediate allocator run.

The final standard Aquarius/Python/Node.js report also passed all 30 cases, using
five measured samples and one discarded process warmup. It selects the compiled
Wasm backend and records `precompiled` mode and CPU affinity. Its published report
is in the sibling performance benchmark repository, under
`results/20261010-212534-926618/results.json`; the previous legacy report is
preserved under `results/legacy-before-wasm-v2`.

Raw evidence: [process comparison](comparison.json), [Wasm warm profile](warm.json),
[legacy warm profile](legacy-warm.json). These include samples, correctness,
binary/source/module fingerprints, runtime version and CPU affinity. Reproduction
instructions are in [the profiler guide](../../README.md).

The [migration progress](../../../../docs/wasm-migration-progress.md) records
remaining portable libraries, library ABI/linking, C# removal and platform work.
