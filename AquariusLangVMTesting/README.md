# Aquarius VM tests

This dedicated test project copies every C# test source from `AquariusLangTesting`,
including lexer, parser, AST, object, utility, evaluator, Chinese syntax, indexed
assignment, module, desktop interpreter, OpenGL and Processing coverage.
Copied runtime tests alias `Evaluator` to `AquariusLang.VM.VmEvaluator` and reference
`AquariusDesktopVMREPL`, so they exercise compiled execution, imports and callbacks.
Examples and native runtime assets arrive through the VM desktop project reference.
Retain matching assertions when adding coverage to either test suite.

`VM/CompilerAndMachineTest.cs` adds compiled-instruction checks, execution after AST
mutation, fresh state when reusing bytecode, persistent REPL state, 20,000 recursive
calls, nested loop breaks, loop closures, early error termination, native callback
reentry and comparisons against the original evaluator.

```powershell
dotnet test AquariusLang.sln -c Release -m:1
dotnet test AquariusLangVMTesting -c Release -m:1
```

The existing external-process test requires `python` on PATH. Enable native GPU
integration tests, including Processing callbacks and the showcase, with:

```powershell
./native/build.ps1
$env:AQUARIUS_OPENGL_TESTS = '1'
dotnet test AquariusLangVMTesting -c Release -m:1 --filter FullyQualifiedName~AquariusREPL.Graphics
Remove-Item Env:AQUARIUS_OPENGL_TESTS
```

An opt-in benchmark compares the tree evaluator with bytecode execution for an
integer loop, recursive Fibonacci and closures with array writes. It verifies
matching results, warms up both engines, alternates execution order, and reports
the median of nine runs plus compilation time. Timing is informational and has no
pass/fail threshold. Compilation is excluded from execution time; short scripts
may not benefit from compilation.

Release measurements on Windows x64 with .NET 6.0.36 (2026-10-07):

| Sample | Tree evaluator median | VM median | Execution speedup |
| --- | ---: | ---: | ---: |
| Integer loop (50,000 iterations) | 30.72 ms | 25.12 ms | 1.22x |
| Recursive Fibonacci (20) | 13.97 ms | 8.76 ms | 1.59x |
| Closures and array writes (20,000 iterations) | 18.31 ms | 14.19 ms | 1.29x |

These measurements describe these workloads on this machine, not a universal
speedup guarantee. The complete Release solution passed 142 interpreted tests and
171 VM tests with `AQUARIUS_OPENGL_TESTS=1`, including all seven GPU cases per suite.

```powershell
$env:AQUARIUS_VM_BENCHMARKS = '1'
dotnet test AquariusLangVMTesting -c Release -m:1 --filter FullyQualifiedName~BenchmarkTest --logger 'console;verbosity=detailed'
Remove-Item Env:AQUARIUS_VM_BENCHMARKS
```
