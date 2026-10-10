# Compiled call conventions

The compiler emits Wasm calls to the versioned host ABI. Dynamic values and
argument evaluation preserve bilingual aliases, lexical capture, arity checks,
eager evaluation order and result identity. Script calls suspend to compiled
continuation frames, so deep recursion does not grow the host call stack.

Desktop host functions use `BuiltinObj` and may opt into borrowed argument spans.
Calls receive independent argument storage, including callback reentry. A compiled
callback refers to its Wasm module/function and defining environment; each reentrant
execution owns its Wasm store and imports. Wasmtime modules are weakly cached by
artifact identity, while execution stores are disposed after every run.

Browser bindings retain their Promise API and expose a synchronous `nativeCall`
entry. Synchronous results return immediately; Promise/thenable results suspend
and then resume the compiled continuation. Cancellation is checked at compiled
block boundaries, and browser checkpoints yield during pure computation.

Tests: `AquariusTests/compiler/CallConventionTest.cs`, compiler/closure language
cases, cross-engine Wasm parity, and `AquariusWebCompiler/tests/wasm.test.mjs`.
Measure Wasm emission and execution separately with `AQUARIUS_WASM_BENCHMARKS=1`.
