# VM call conventions

Aquarius call instructions use a stack window containing the callee followed by
its evaluated arguments. Both runtimes bind script parameters directly from that
window before removing it. Argument order, alias identity, captured environments,
return values, and arity checks remain unchanged. No bytecode or bottle format
change is required; already compiled packages use the optimized path.

## Desktop native calls

`BuiltinObj.FromBorrowed(BorrowedBuiltinFunction)` opts a synchronous host function
into a `ReadOnlySpan<IObject>` argument window. Processing uses this convention
for its registered functions, including math, colors, transforms, and primitives.
The callback may retain individual object references or explicitly copy values;
it cannot retain the span. Each VM execution owns its stack, so callback reentry
uses independent storage. Argument references are cleared after the call.

The existing `BuiltinObj(BuiltinFunction)` and `Fn(IObject[])` API remains available.
Legacy functions called by the VM still receive independent arrays, which they
may retain or modify. Setting `Fn` invalidates the borrowed entry point. Public
`Invoke` continues to accept the existing argument array interface.

This mechanism lives in the language core and is optional for every host. It does
not introduce graphics, operating-system, or GPU dependencies into the core.

## Browser native calls

Browser host bindings keep their public Promise-returning API. An internal symbol
exposes a direct entry point to the VM. Synchronous results enter the operand
stack immediately; Promise and thenable results suspend and resume execution.
Script calls push frames without a Promise round trip. Common native arities
avoid a separate operand-stack argument array.

Asynchronous imports, GPU readback, dialogs, errors, cancellation, bilingual
aliases, and raw result identity keep their existing behavior. The instruction
budget still yields to the browser event loop. Incidental microtask checkpoints
at synchronous script/native calls are removed; host integrations must use actual
asynchronous results when suspension is required.

## Verification and measurement

- `VM/CallConventionTest.cs` covers retained legacy arrays, callback reentry,
  large argument windows, aliasing, replaced native functions, errors, argument
  evaluation order, and serialized programs.
- `call-conventions.test.mjs` covers synchronous execution, public Promise
  compatibility, aliases, arities, raw results, asynchronous thenables, errors,
  callback reentry, and cancellation safepoints.
- The existing core, desktop graphics, bytecode parity, and browser smoke tests
  continue to exercise the same public language and host interfaces.

Desktop allocation measurements are opt-in:

```powershell
$env:AQUARIUS_VM_BENCHMARKS = '1'
dotnet test AquariusLangVMTesting -c Release --filter FullyQualifiedName~CallConventionTest --logger 'console;verbosity=detailed'
```

Browser call measurements isolate the public Promise path and direct VM path:

```powershell
node AquariusWebCompiler/tests/call-performance.mjs
```

An optional real-WebGPU Painter benchmark serves an existing export unchanged,
then substitutes only the candidate VM and host modules. It finishes the animation
loop before manually measuring frames, waits for GPU completion, and disposes the
device between variants:

```powershell
node AquariusWebCompiler/tests/painter-call-performance.mjs C:/OfficialProjects/AquariusLang_Painter
```

These are informational timings, not correctness thresholds. Call microbenchmarks
do not establish end-to-end drawing speedups: GPU allocation, readback, resource
reclamation, and rendering can dominate Painter's latency. Measure those costs
separately before attributing an application-level speedup to the interpreter.
