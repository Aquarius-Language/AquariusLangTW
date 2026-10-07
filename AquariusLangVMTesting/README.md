# Aquarius VM tests

This is the solution's .NET 8 (`net8.0`) test project. Build and run the tests with
the .NET 8 SDK, which includes the required runtime. It references `AquariusLangVM` and
`AquariusDesktopVMREPL` and exercises the frontend, compiled execution, desktop
imports and callbacks.

- `ast/`, `lexer/`, `object/`, `parser/`, `utils/` and root syntax tests cover the language core.
- `VM/VmEvaluatorTest.cs` covers runtime language behavior through `VmEvaluator`.
- `VM/CompilerAndMachineTest.cs` and `VM/DesktopImportTest.cs` cover compilation, execution and imports.
- `desktop/runtime/` covers the script runner, desktop builtins and Starship example.
- `desktop/graphics/` covers OpenGL and Processing, with optional GPU integration.

The desktop assembly grants the test assembly access to internal graphics types
through `InternalsVisibleTo`. Examples and native runtime assets arrive through
the VM desktop project reference. Resolve examples from `AppContext.BaseDirectory`
and keep supporting assets beside their scripts.

`VM/CompilerAndMachineTest.cs` adds compiled-instruction checks, execution after AST
mutation, fresh state when reusing bytecode, persistent REPL state, 20,000 recursive
calls, nested loop breaks, loop closures, early error termination, native callback
reentry and explicit expected results for control flow, assignment and boolean operators.

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

An opt-in benchmark measures VM execution for an integer loop, recursive Fibonacci
and closures with array writes. It verifies known numeric results, warms up the
VM, and reports the median of nine executions with fresh environments. Parsing
and compilation are measured separately and excluded from execution timing.
Timing is informational and has no pass/fail threshold; results depend on the
machine and runtime. The benchmark uses only the production VM.

```powershell
$env:AQUARIUS_VM_BENCHMARKS = '1'
dotnet test AquariusLangVMTesting -c Release -m:1 --filter FullyQualifiedName~BenchmarkTest --logger 'console;verbosity=detailed'
Remove-Item Env:AQUARIUS_VM_BENCHMARKS
```

The language server and VS Code extension own their Node.js tests:

```powershell
dotnet build AquariusLanguageServer -c Release
node --test AquariusLanguageServer/tests/lsp.test.js
npm --prefix editors/vscode run check
```
