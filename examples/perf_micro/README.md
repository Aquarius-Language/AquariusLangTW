# Micro-benchmarks / 微基準測試

Small untyped programs for runtime paths the standard benchmark suite
(`AquariusLang_PerformanceBenchmark`) does not cover: hashes with 100k string and integer keys, building a string from
100k pieces with `s = s + t`, string indexing, sorting 1M integers (written in Aquarius and with `陣列.排序`), struct field
access, helper-function calls and array sums. Each prints `RESULT` and `ELAPSED_MS` like the benchmark scripts.

```powershell
examples/perf_micro/run-micro.ps1 -Aqua compiler/AquariusCli/bin/Release/net8.0/aqua.exe -Runs 5
```

The script builds each program once and prints the median of `-Runs` runs. Results of the performance rounds are in
`reports/`.
