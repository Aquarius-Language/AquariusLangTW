# AquariusCore

Portable compiler and runtime contracts targeting .NET 8. Core has no native engine,
graphics or physics package dependencies.

Source flows through the lexer/parser, `LoweringCompiler` and compiler-only IR to
`WasmCompiler`, which emits a standard WebAssembly module. `WasmProgram` loads and
validates versioned application metadata. `WasmRuntime` supplies dynamic values,
scopes, closures and continuation scheduling; an `IWasmEngine` adapter executes the
compiled functions. Desktop registers Wasmtime; browsers use native WebAssembly.

```csharp
using AquariusLang.Wasm;

var program = new WasmCompiler().Compile("變數 答案 = 6 * 7; 答案;");
File.WriteAllBytes("app.wasm", program.Bytes.ToArray());
// Host composition supplies an IWasmEngine, such as WasmtimeEngine.
var runtime = new WasmRuntime(engine: engine);
Console.WriteLine(runtime.Execute(program).Inspect()); // 42
```

`CompiledEvaluator` is a compile-and-run convenience API for interactive sessions
and source imports. Its cache is keyed by AST identity; compiled closures retain
parameters, environments and Wasm function identities, without retaining AST bodies.
`CompiledRuntime` also accepts compiler IR for tools and tests, always emitting Wasm
before execution. IR is never serialized or interpreted.

Core owns WebGPU and application contracts, shared shaders and validation. It also
owns external library descriptors, the shared Jolt world implementation and pinned
Emscripten assets. Run `npm ci --prefix AquariusWebCompiler` and
`npm run --prefix AquariusWebCompiler prepare:browser` before building to prepare
those generated assets from the lockfile.

See [architecture](../ARCHITECTURE.md), [language behavior](LANGUAGE.md),
[Wasm format](../AquariusPackaging/README.md) and
[external libraries](../docs/external-libraries.md).
