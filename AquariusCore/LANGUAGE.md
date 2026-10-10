# Language core and WebAssembly development

The portable language implementation lives entirely in `AquariusCore`.
The desktop host and language server use the same frontend and object model.
The language server analyzes source without running it; the desktop host compiles
and executes scripts, imported modules and callbacks through WebAssembly.

## Source and execution pipeline

The lexer and parser construct an AST. `LoweringCompiler` produces compiler-only
IR, and `WasmCompiler` emits native Wasm functions, structured control flow,
versioned host imports and application metadata. The deployed artifact is `.wasm`;
Aquarius instructions and AST bodies are not interpreted at runtime.

`WasmRuntime` owns dynamic values, lexical environments and compiled continuations.
An `IWasmEngine` executes generated exports; the desktop adapter uses Wasmtime,
while browsers use native WebAssembly. Calls can suspend for browser host services,
and deep recursion uses explicit continuation frames. Native callbacks reenter with
independent execution state. Function objects retain parameters, captured environments
and inspection text, and reference compiled function indices instead of AST bodies.

`CompiledEvaluator` compiles source or AST input and preserves optional session
environments. Treat a cached AST as immutable. `CompiledRuntime` accepts Wasm or
compiler IR for tool integration; IR always goes through Wasm emission before execution.
See [the architecture](../ARCHITECTURE.md) and [ABI](../AquariusPackaging/README.md).

## Language behavior

- Keywords include `變數`, `函式`, `如果`, `否則如果`, `否則`, `回傳`, `迴圈`, `中斷`, `真` and `假`.
- Arithmetic promotes mixed numeric operands to double, then float, then integer.
  Compound numeric assignments preserve the left variable's numeric type by
  converting the result back to that type. Prefix/postfix `++` also preserve it.
- `++值` returns the updated number; `值++` returns the original number.
  Both require an existing numeric variable.
- Boolean `&&` and `||` evaluate both operands. They do not short-circuit.
- Functions support explicit `回傳`, a final expression as an implicit result,
  recursion and lexical closures. Assignments update the scope owning a variable.
- `變數 加法, add = 函式(甲, 乙) { 甲 + 乙; };` gives one function two public
  names, in either order. A single name continues to work. Two-name declarations
  require a function literal and distinct identifiers. Both bindings receive the
  same function object and captured environment; subsequent assignment to one
  binding follows ordinary variable semantics and does not rebind the other.
  Declared names and parameters shadow global builtins. Module member calls look
  up the receiver's exports, so aliases such as `vector.長度()` cannot accidentally
  call an unrelated global builtin.
- A loop's declared control variable stays local to the loop and carries its value
  into the next iteration. Body declarations get a fresh scope each iteration;
  closures can retain the environment from the iteration that created them.
- Arrays support indexed writes. Hash keys use `IHashable` and `HashKey` values.
  Out-of-range array reads and missing hash entries return `NullObj`; invalid
  array writes return a language error.
- Imported script modules expose their environment through `ModuleObj`.
  Module member calls evaluate arguments in the caller's environment and execute
  function bodies in their captured environment.
- Runtime failures produce `ErrorObj` and stop execution before later side effects.
  Compiler syntax failures raise `CompilationException`; `CompiledEvaluator` converts
  them to `ErrorObj` for hosts using its evaluation APIs.

See the repository [syntax and examples](../README.md#語法) and the
[Starship example](../AquariusDesktop/examples/starship_expedition/README.md).

## Extending the language

When introducing syntax, update tokens, lexer rules, parser callbacks and operator
precedences, then the AST and `LoweringCompiler`. Add an opcode or `ValueOperations` behavior
when needed, and update language-server facts and editor highlighting/snippets.

Keep AST nodes as classes to preserve the current mutable node identity and
reference behavior. `HashKey` is a value type with value equality for dictionary
lookup; a replacement reference type must implement consistent equality and
hashing. Avoid relying on reference identity for equivalent hash keys.

Be careful about `NextToken` calls in parser branches, particularly loop parsing.
New prefix/infix operators must be registered at the appropriate precedence.
`List.Append` returns an enumerable; use `Add` to mutate an instruction or node list.

Verify frontend behavior and Wasm execution in `AquariusTests`. Include
compiler and runtime cases for changes affecting stack balance, jump targets,
scopes, errors or calls. Update the comprehensive Starship example when introducing
new language tokens. Run [the test suite](../AquariusTests/README.md).

## Host libraries and future ideas

Host functions use `BuiltinObj` and the `AquariusLang.runtime.Builtins` registry.
Desktop-only facilities, imports and graphics belong in `AquariusDesktop`;
keep the portable library free of native desktop dependencies. Hosts must dispose
`DesktopBuiltins` to release graphics resources. Native callbacks invoke Aquarius
functions through `CompiledEvaluator.Invoke`.

Host libraries can register either or both names with the portable API:

```csharp
var function = new BuiltinObj(args => new IntegerObj(42));
FunctionRegistration.Define(moduleEnvironment, function,
    traditionalChineseName: "答案", englishName: "answer");
FunctionRegistration.Define(moduleEnvironment, anotherFunction, englishName: "englishOnly");
builtins.DefineFunction(function, traditionalChineseName: "答案", englishName: "answer");
```

Omit a name with `null`. Empty names, keywords, duplicate names and collisions
with existing exports fail before any binding is added. The registry accepts
native `BuiltinObj` and compiled `FunctionObj` callables and preserves identity.
`LibraryCatalog` supplies metadata shared by the desktop host and LSP without
loading native libraries. Update `native/generate_library_catalog.py`, then run
it to regenerate the catalog and naming reference when changing library APIs.
OpenGL aliases retain the `gl` prefix and overload/type suffixes; singular and
plural entry points have distinct names. The appended `Duplicate` opcode binds
two names to one closure and is validated during `.aqua` loading. Existing
bilingual names share one compiled closure and remain identical across exported Wasm applications.

`FunctionRegistration.Replace` explicitly rebinds an existing alias group during
library reloads. It checks that all names refer to the same old function before
replacing them; Processing uses this when a closed sketch starts again.

Possible future work includes Git, cross-language interoperability, calculus
operators, desktop GUI libraries, PyTorch bindings and additional graphics/game
libraries such as Wicked Engine, sokol, Raylib, Magnum, SFML/SDL2 or MonoGame.
JIT/AOT, WASI, libgccjit or code generation through other languages remain
exploratory options. The compiler emits standard WebAssembly, with platform engines and versioned capability contracts. Future optimizers can specialize value operations while retaining the host ABI.