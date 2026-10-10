# Wasm migration implementation status

Updated October 10, 2026. The portable execution core is implemented and tested.
**The complete 60-point migration is not finished.** The compiler, desktop host
and several existing library implementations still require C#/.NET or JavaScript.
The requirement to retain C# only for justified native integrations is not yet
met. Recompile the old ABI 1 application artifacts.

## Implemented

- A freestanding C runtime compiled to Wasm is linked into every application.
  Values, scopes, closures, arrays, hashes, script calls, loop scopes, scheduling
  and garbage collection execute in Wasm on desktop and browser engines.
- Functions, parameters and constants live in Wasm sections. The emitter produces
  direct arithmetic/comparisons, numeric Wasm locals, constant loads, structured
  branches and guarded guest fallbacks. Operand-depth analysis sizes frames.
- One aquarius_v2.service import replaces the old per-operation host imports.
  The C# and JavaScript execution machines and unused C# ValueOperations were
  removed. Compiler IR remains a compiler detail, with semantic tests retained.
- Portable len, last, rest and functional push execute in Wasm. Appended arrays
  share storage and detach before guest or host writes. Mutable aliases retain
  identity; independent appended arrays preserve snapshots.
- Small scopes use direct lookup; larger scopes use collision-safe indexes.
  Loop frames cache binding addresses behind a generation guard that invalidates
  on structural scope changes, including callback reentry.
- The nonmoving collector traces suspended arguments and cyclic containers.
  Size-class free lists reuse allocations; bulk-memory operations copy data;
  allocation pressure triggers collection at safe guest checkpoints.
- Transport supports callback reentry, asynchronous browser suspension,
  cancellation, projected scopes, cyclic objects, array resizing and hash edits.
  Changed pixel buffers transfer to their owning capability. Full typed hash
  keys replace CLR hash-code identities.
- Native handles recycle after collection. Anonymous runs avoid host scope pins.
  Capability contexts reuse identities, native callbacks weakly reference their
  session, and explicit disposal releases stores. Packaged applications own one
  runtime through service shutdown.
- Desktop console programs initialize graphics lazily. Wasmtime owns persistent
  compilation caching and compatibility checks.
- Future computational libraries cannot enter the CLR factory registry as
  CoreWasm; new Emscripten providers are rejected. This is an admission gate,
  not a completed generic Wasm library loader.
- The pinned Zig source/binary reproducibility check passed with runtime SHA-256
  2ac77fff9ce91ae938f095dcc90a3eee1f9b3cfa46778bfa7c6c24ffc07fcff0.

The [ABI 2 guide](wasm-value-abi-v2.md) documents values, calls, memory,
collection and transport. There is no fallback interpreter for ABI 1 artifacts.

## Final validation

Validation is on Windows 11 x64 and Chrome with WebGPU, not proof of Linux/macOS
or other-browser deployment.

| Check | Result |
| --- | --- |
| Desktop unit, language, packaging, CLI and opt-in native tests | 1,241 passed; zero failures or skips |
| Browser unit and ABI conformance | 116 passed |
| Language-server tests and example parsing | 15 passed |
| Desktop examples, source and precompiled Wasm | 136/136 passed |
| Browser examples, rendering, gameplay, cancellation and assets | 71/71 passed |
| All three staged desktop Wasm and EXE checks | Passed |
| Actual application repositories' desktop Wasm and EXE checks | Passed |
| MarbleRun integration assertions | Passed |
| Snake rules | 236 passed |
| All three Chrome rendering/control/resize/shutdown smoke checks | Passed |
| Painter full browser interaction suite | 24/24 passed |
| Snake full browser gameplay suite | 16/16 passed |
| Benchmark harness tests | 10/10 passed |
| Final standard Aquarius/Python/Node.js benchmark | All 30 cases passed |

Old boxed-number identity assertions and per-operation host-VM assumptions were
replaced with ABI 2 value and guest-execution checks. Language, lowering and native
capability tests were retained. Source-mirror checks normalize line endings.
Painter's browser test helper uses integer transport only for signed 32-bit
values; larger packed colors use double transport.

Both web and desktop outputs were regenerated in the actual MarbleRun, Painter
and Snake repositories. Their standalone EXEs use the rebuilt ABI 2 runtime pack.
Painter's startup smoke uses one rendered frame because setup disables continuous
drawing; later frames require input. Full interaction validation is automated in
the browser; desktop validation uses native tests and rendered startup checks.

Raw logs, captures and results are under .web-build/migration, .web-build/projects,
.web-build/browser-results.json, .web-build/desktop-example-results.json and
AquariusTests/TestResults.

## Measured performance

The final comparison uses logical CPU 0, the unchanged Balanced power plan,
preserved legacy binaries and current Wasm binaries. It validates every output
and records fingerprints and individual samples.

- Warmed execution: **1.744x faster** geometric mean over ten workloads.
- Precompiled-Wasm process execution: **1.128x faster** overall.
- Source-to-Wasm process execution: **1.002x**, effectively unchanged overall.
- Binary trees and matrix still regress when warmed. Several process workloads
  retain startup and execution regressions.
- Each numeric kernel makes one native call for final print, with one or two
  capability lookups. Computational operations stay inside Wasm.
- The final three-language report identifies compiled-wasm and precompiled mode.
  The earlier legacy report was preserved before publishing.

See [the measurements and raw profiles](../benchmarks/wasm-runtime/measurements/20261010/README.md).
These are Windows measurements with a populated compilation cache, not empty-cache
first-launch or cross-platform guarantees. The all-workload performance gate
has not passed.

## Remaining completion requirements

- Compiler-resolved lexical slots, typed data-flow/liveness and specialization,
  direct-call optimization and indexed collection hashes. Guarded binding caches
  do not replace resolved lexical slots.
- A validated Wasm library manifest, actual loader/linker, dependency locks,
  capability and feature admission, resource/error contracts, reproducible
  library builds and bulk buffer ownership ABI. Future library computation and
  Aquarius bindings must be Wasm, including transitive computational dependencies.
- Existing portable library algorithms/bindings: geometry/math, text,
  serialization/compression, image processing and application helpers. Jolt still
  uses Emscripten glue and desktop ClearScript/V8; existing algorithms retain
  earlier JS/native/managed adapters. The admission gate is not their port.
- Portable compiler, CLI and LSP replacement and a native launcher/host. C#/.NET
  remains mandatory in the transitional toolchain and desktop deployment. Native
  integration exceptions need individual justification; existing C# code is
  not itself a justification.
- Desktop asynchronous completion, thread affinity and cancellation contracts;
  final host transport/resource ownership and teardown contracts.
- Linux/macOS clean-machine deployment and supported browser conformance.
- Remaining warm regressions, cold startup and full performance gates.

See [the complete migration backlog](wasm-architecture-migration.md) for scope.
