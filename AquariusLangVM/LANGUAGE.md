# Language core and VM development

The portable language implementation lives entirely in `AquariusLangVM`.
The desktop host and language server use the same frontend and object model.
The language server analyzes source without running it; the desktop host compiles
and executes scripts, imported modules and callbacks on the VM.

## Source and execution pipeline

| Component | Responsibility |
| --- | --- |
| `token/`, `lexer/` | Chinese keywords, Unicode identifiers, literals, comments and tokens |
| `parser/`, `ast/` | Operator precedence, statements, expressions and syntax trees |
| `object/` | Values, arrays, hashes, functions, modules and lexical environments |
| `runtime/` | Host builtin registry and shared primitive values |
| `VmCompiler.cs`, `Bytecode.cs` | Instruction generation, constant pools, jumps and compiled function bodies |
| `VirtualMachine.cs`, `VmOperations.cs` | Operand stack, call frames, scopes and runtime operations |
| `VmEvaluator.cs` | Source/AST entry points, weak compilation caches and native callback invocation |

`VmCompiler.Compile(source)` lexes and parses before generating bytecode.
`Compile(tree)` accepts an existing AST. Function bodies are compiled along with
the surrounding program. VM closures retain their lexical environment and are
associated with compiled body instructions. The AST body also remains available
for inspection. `VmEvaluator.Eval` caches bytecode by AST identity, so treat a tree
as immutable after evaluating it. Recompile explicitly after editing an AST.

A compiled program can be executed repeatedly. Supply a fresh environment for
independent runs or reuse one for a persistent session. Each execution owns its
operand stack and call frames, including when a native callback reenters the VM.
Aquarius recursion uses VM frames instead of recursive C# evaluation.

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
- A loop's declared control variable stays local to the loop and carries its value
  into the next iteration. Body declarations get a fresh scope each iteration;
  closures can retain the environment from the iteration that created them.
- Arrays support indexed writes. Hash keys use `IHashable` and `HashKey` values.
  Out-of-range array reads and missing hash entries return `NullObj`; invalid
  array writes return a language error.
- Imported script modules expose their environment through `ModuleObj`.
  Module member calls currently evaluate arguments in the module environment;
  return a function and call it locally when arguments need the caller's bindings.
- Runtime failures produce `ErrorObj` and stop execution before later side effects.
  Compiler syntax failures raise `VmCompilationException`; `VmEvaluator` converts
  them to `ErrorObj` for hosts using its evaluation APIs.

See the repository [syntax and examples](../README.md#語法) and the
[Starship example](../AquariusDesktopVMREPL/examples/starship_expedition/README.md).

## Extending the language

When introducing syntax, update tokens, lexer rules, parser callbacks and operator
precedences, then the AST and `VmCompiler`. Add an opcode or `VmOperations` behavior
when needed, and update language-server facts and editor highlighting/snippets.

Keep AST nodes as classes to preserve the current mutable node identity and
reference behavior. `HashKey` is a value type with value equality for dictionary
lookup; a replacement reference type must implement consistent equality and
hashing. Avoid relying on reference identity for equivalent hash keys.

Be careful about `NextToken` calls in parser branches, particularly loop parsing.
New prefix/infix operators must be registered at the appropriate precedence.
`List.Append` returns an enumerable; use `Add` to mutate an instruction or node list.

Verify frontend behavior and VM execution in `AquariusLangVMTesting`. Include
compiler and runtime cases for changes affecting stack balance, jump targets,
scopes, errors or calls. Update the comprehensive Starship example when introducing
new language tokens. Run [the test suite](../AquariusLangVMTesting/README.md).

## Host libraries and future ideas

Host functions use `BuiltinObj` and the `AquariusLang.runtime.Builtins` registry.
Desktop-only facilities, imports and graphics belong in `AquariusDesktopVMREPL`;
keep the portable library free of native desktop dependencies. Hosts must dispose
`DesktopBuiltins` to release graphics resources. Native callbacks invoke Aquarius
functions through `VmEvaluator.Invoke`.

Possible future work includes Git, cross-language interoperability, calculus
operators, desktop GUI libraries, PyTorch bindings and additional graphics/game
libraries such as Wicked Engine, sokol, Raylib, Magnum, SFML/SDL2 or MonoGame.
JIT/AOT, WASI, libgccjit or code generation through other languages remain
exploratory options. The current compiler emits in-memory VM instructions;
serialized bytecode, native code generation and these additional integrations
are not implemented.
