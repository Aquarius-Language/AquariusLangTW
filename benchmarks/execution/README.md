# Execution performance diagnostics

Build the Release CLI first, then build this standalone diagnostic project. It
uses the same compiler and desktop adapter as the CLI. `AquariusHost` can select
another directory containing the Core, Desktop and Wasmtime assemblies.

```powershell
dotnet build AquariusCli -c Release -m:1
dotnet build benchmarks/execution -c Release
$profile = 'benchmarks/execution/bin/Release/net8.0/ExecutionProfile.dll'
$benchmarks = '../AquariusLang_PerformanceBenchmark'
dotnet $profile compile $benchmarks .performance/before .performance/before.json
# Preserve those Wasm files, then rebuild with the candidate compiler/host.
dotnet $profile compile $benchmarks .performance/after .performance/after.json
dotnet $profile pair $benchmarks '.performance/before;.performance/after' .performance/paired.json
dotnet $profile memory $benchmarks unused .performance/desktop-memory.json
node benchmarks/execution/host-views.mjs .performance/after/fibonacci.wasm .performance/browser-views.json
```

`compile` preserves the standard benchmark sources as Wasm artifacts and measures
them with five in-process warmups and eleven samples. `pair` alternates preserved
artifacts on the **same host**, using five warmups and fifteen samples per variant.
Every computation is checked against the benchmark repository's reference result.
The original scripts and their timing boundaries are unchanged. JSON includes
samples, module hashes and source hashes. Keep competing benchmarks/builds/tests
idle while measuring.

The `memory` and JavaScript measurements isolate primitive guest-value reads;
their speedups are **not whole-application speedups**. Run `memory` with preserved
baseline and candidate host assemblies to compare desktop adapters. The JS script
alternates fresh DataViews with the current cached-view implementation.

For the final process-isolated, three-language benchmark, use the original
benchmark runner with an explicit freshly built `--aquarius` executable. PATH
discovery can otherwise silently select an older installed release. Each runner
sample starts a new process; its warmup process does not warm later processes.
