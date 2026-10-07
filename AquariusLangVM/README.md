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
bytecode; `Disassemble` is a readable listing, not a serialized executable format.

For a persistent session, create `Builtins` from `AquariusLang.runtime`, use
`VmEvaluator.NewInstance(builtins)` and call
`Evaluate(source, environment)` or `Eval(tree, environment)`. `Invoke` runs Aquarius
functions passed to native libraries, including callbacks that reenter the VM.
`FunctionObj` retains its parameters, AST body and lexical environment for
inspection; its compiled body is associated with the closure by `VmEvaluator`.
Runtime failures produce `ErrorObj`; invalid source passed directly
to the compiler raises `VmCompilationException`.

Language behavior includes Chinese syntax, integer/float/double promotion,
prefix/postfix increment, compound assignment, eager boolean operators,
conditional expressions, loops and break, return, lexical closures, recursion,
mutable array writes, hash indexing and module member calls. Loop declarations
persist between iterations; body declarations get a fresh scope each iteration.

Frontend development and language semantics: [language core guide](LANGUAGE.md).
Desktop integration: [AquariusDesktopVMREPL](../AquariusDesktopVMREPL/README.md).
Language coverage, VM regression tests and benchmarks:
[AquariusLangVMTesting](../AquariusLangVMTesting/README.md).
