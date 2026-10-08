# AquariusLangVM

Aquarius language core, bytecode compiler and stack virtual machine. Targets
.NET 8 (`net8.0`) with no project or native graphics dependencies.

This project owns the lexer, parser, AST, environments, objects and builtin
interface. Source flows through the lexer and parser to an AST, then to bytecode.
`VmCompiler` translates all language constructs into a compact instruction stream
with a constant pool. `VirtualMachine` executes those instructions with an operand
stack and explicit call frames; Aquarius recursion does not recurse on the C# stack.
Function bodies are compiled before execution. `VmEvaluator` caches compiled syntax
trees with weak references for desktop imports, REPL evaluation and callbacks.

```csharp
using AquariusLang.VM;

var program = new VmCompiler().Compile("變數 答案 = 6 * 7; 答案;");
Console.WriteLine(program.Disassemble());
var result = new VirtualMachine().Execute(program);
Console.WriteLine(result.Inspect()); // 42
```

Compile once and call `Execute` repeatedly with separate environments. Arrays,
hashes and closures are created on each execution. Instructions are in-memory
bytecode; `Disassemble` is a readable listing. `BytecodeSerializer.Write(program,
stream)` saves a versioned binary `.rius` file, and `BytecodeSerializer.Read(stream)`
restores it without parsing source. Both operations leave the supplied stream
open. The desktop CLI packages these files into ZIP-compatible `.bottle` archives
using `-c` or `-cr`; direct source execution continues to use in-memory bytecode.

For a persistent session, create `Builtins` from `AquariusLang.runtime`, use
`VmEvaluator.NewInstance(builtins)` and call
`Evaluate(source, environment)` or `Eval(tree, environment)`. `Invoke` runs Aquarius
functions passed to native libraries, including callbacks that reenter the VM.
`FunctionObj` retains its parameters and lexical environment. Functions compiled
from source retain their AST body for inspection; functions loaded from `.rius`
use saved inspection text and have no AST body. `VmEvaluator` associates compiled
instructions and builtin context with each closure, including native callbacks.
Runtime failures produce `ErrorObj`; invalid source passed directly
to the compiler raises `VmCompilationException`.

Language behavior includes Chinese syntax, integer/float/double promotion,
prefix/postfix increment, compound assignment, eager boolean operators,
conditional expressions, loops and break, return, lexical closures, recursion,
mutable array writes, hash indexing and module member calls. Loop declarations
persist between iterations; body declarations get a fresh scope each iteration.

Frontend development and language semantics: [language core guide](LANGUAGE.md).
Desktop integration: [AquariusDesktopVMREPL](../AquariusDesktopVMREPL/README.md).
Graphics hosts share the renderer-independent `GraphicsSurfaceSize` contract:
logical dimensions drive layout, input and cameras; physical dimensions drive
GPU attachments and presentation. Hosts sample sizes at frame boundaries,
reconfigure targets before resize callbacks, redraw paused sketches and skip
zero-pixel surfaces while retaining logical dimensions. Window handles, DOM and
native GPU types stay outside the core. The browser implementation is checked
against the core with resize/density/minimize/restore parity tests.
Language coverage, VM regression tests and benchmarks:
[AquariusLangVMTesting](../AquariusLangVMTesting/README.md).
