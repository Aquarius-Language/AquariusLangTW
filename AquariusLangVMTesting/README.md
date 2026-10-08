# Aquarius VM tests

This is the solution's .NET 8 (`net8.0`) test project. Build and run the tests with
the .NET 8 SDK, which includes the required runtime. It references `AquariusLangVM` and
`AquariusDesktopVMREPL` and exercises the frontend, compiled execution, desktop
imports and callbacks.

- `ast/`, `lexer/`, `object/`, `parser/`, `utils/` and root syntax tests cover the language core.
- `VM/VmEvaluatorTest.cs` covers runtime language behavior through `VmEvaluator`.
- `VM/CompilerAndMachineTest.cs` and `VM/DesktopImportTest.cs` cover compilation, execution and imports.
- `desktop/runtime/` covers the script runner, desktop builtins and Starship example.
- `VM/BytecodeSerializerTest.cs` covers versioned `.rius` round trips, nested
  closures, module member resolution, control flow, truncated/corrupt data,
  instruction operands and stack/loop validation.
- `desktop/runtime/BottlePackageTest.cs` and `BottleRuntimeTest.cs` cover archive
  layout, Unicode paths, atomic replacement, invalid manifests, relocated bottles,
  source-free imports, deferred imports from closures, circular imports and the
  comprehensive Starship example.
- `desktop/runtime/BottleCliSmokeTest.cs` launches the desktop executable in a
  separate process to check `-c`, `-cr`, later execution, entry selection, output,
  exit codes, invalid options and existing REPL/source/disassembly commands.
- `desktop/graphics/` covers OpenGL, WGPU and Processing, with optional GPU integration.
  `WgpuTest.cs` checks imports, WGSL uniform layout, clipping, example parsing,
  GPU rendering/compute/readback, resource ownership and cleanup, plus separate
  CLI source/bottle smoke runs. Processing integration now exercises wgpu.
- `desktop/physics/` runs the real Jolt engine by default, checking gravity,
  motion, impulse/mass, force clearing, angular inertia, restitution, contacts,
  input validation, body ownership and native resource cleanup. Its CLI smoke
  tests execute source and source-free bottles in separate processes.
- `BilingualFunctionTest.cs` covers optional single/dual names, parser errors,
  alias identity, closures, recursion, callbacks, shadowing and serialized code.
  `BilingualLibraryTest.cs` checks every native function's aliases and argument
  validation, plus catalog coverage for module and object exports.
  Existing GLM, GL buffer, Processing, text-input and Jolt cases also execute
  Traditional Chinese calls. `BilingualCliSmokeTest.cs` runs mixed/single-language
  libraries and the bilingual example as source and source-free bottles.
  GPU opt-in tests validate aliases and shared state of returned WGPU objects,
  Processing canvases/shaders, and Chinese variants of GPU source/bottle examples.

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

# Bilingual functions, native library metadata, source/bottle smoke tests
dotnet test AquariusLangVMTesting -c Release -m:1 --filter FullyQualifiedName~Bilingual

# Native Jolt numerical unit tests and CLI smoke tests (no GPU required)
dotnet test AquariusLangVMTesting -c Release -m:1 --filter FullyQualifiedName~Jolt

# Bytecode packaging unit and process smoke tests
dotnet test AquariusLangVMTesting -c Release -m:1 --filter 'FullyQualifiedName~BytecodeSerializerTest|FullyQualifiedName~Bottle'
```

The existing external-process test requires `python` on PATH. Enable GPU
integration tests, including wgpu compute/rendering, Processing callbacks and
the showcase, and the retained raw OpenGL library, with:

```powershell
./native/build.ps1
$env:AQUARIUS_OPENGL_TESTS = '1'
$env:AQUARIUS_WGPU_TESTS = '1'
dotnet test AquariusLangVMTesting -c Release -m:1 --filter FullyQualifiedName~AquariusREPL.Graphics
Remove-Item Env:AQUARIUS_OPENGL_TESTS,Env:AQUARIUS_WGPU_TESTS
```

WGPU headless tests require a wgpu adapter and the NuGet-provided runtime;
Processing additionally requires the rebuilt GLFW bridge and a desktop.
Without those opt-in flags, GPU tests are skipped explicitly. Module import,
uniform layout, example parsing and CLI disassembly tests run without GPU initialization.
See [the wgpu guide](../AquariusDesktopVMREPL/graphics/WGPU.md).
The `TextInput` tests cover grapheme editing and composition callbacks without
native initialization. With `AQUARIUS_WGPU_TESTS=1`, Windows tests also send native
Unicode/focus/composition lifecycle messages to GLFW_NO_API windows, check queue
overflow and session cleanup, and render committed text through Processing/wgpu.
Actual installed IME preedit/results and candidate selection still need a manual
check with the [multilingual example](../AquariusDesktopVMREPL/examples/multilingual_input/README.md).

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
