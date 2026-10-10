# Wasm execution measurements

`WasmProfile.csproj` measures a reused compiled module and guest session.
`LegacyProfile.csproj` compiles the same source once using a preserved interpreter
release. Both use five in-process warmups and five measured executions and check
every output against the benchmark repository's independent reference answer.
They capture output rather than initializing graphics. Legacy append calls its
actual array-copy helper; Wasm append and length must execute in the guest.

Build from the repository root:

```powershell
dotnet build benchmarks/wasm-runtime/WasmProfile.csproj -c Release
dotnet build benchmarks/wasm-runtime/LegacyProfile.csproj -c Release `
  -p:AquariusLegacyRelease=C:/path/to/preserved/aqua
```

First generate a comparison directory with the performance benchmark repository's
`diagnostics/compare_wasm.py`. It records process startup, source compilation and
precompiled-Wasm modes, with engine compilation inside process timing. With the
enabled Wasmtime cache, a discarded process warmup can populate compiled code;
this is not an empty-cache first-launch measurement.

```powershell
# Optional control for heterogeneous CPU scheduling; applies to child processes.
[Diagnostics.Process]::GetCurrentProcess().ProcessorAffinity = [IntPtr]1
dotnet benchmarks/wasm-runtime/bin/Release/net8.0/WasmProfile.dll `
  C:/path/to/comparison C:/path/to/comparison/warm.json
dotnet benchmarks/wasm-runtime/bin/legacy/net8.0/LegacyProfile.dll `
  C:/path/to/comparison C:/path/to/comparison/legacy-warm.json
```

The profiler records actual CPU affinity and binary fingerprints. Timing excludes
parsing, source compilation, engine compilation and separate instrumentation.
Native-call counts exclude lookups; these numeric kernels should make one native
call for their final print. Managed allocated bytes are total allocation, not
retained or peak memory. Guest heap bytes are a high-water mark.

Compare workload medians and their equally weighted geometric mean. Preserve
regressions and raw samples. CPU affinity, cache state, runtime version and timing
mode must accompany any speedup claim. A favorable overall score does not establish
that every workload or platform improved.
