# Compiled call conventions

The compiler emits Wasm calls to the versioned host ABI. Dynamic values and
argument evaluation preserve bilingual aliases, lexical capture, arity checks,
eager evaluation order and result identity. Script calls suspend to compiled
continuation frames, so deep recursion does not grow the host call stack.

Native library functions (`aqua_define_function`, `<aqua/aqua.h>`) receive their arguments checked against the
declared signature, read them in place and return through the call. Calls receive independent argument storage,
including callback reentry. A compiled callback refers to its Wasm function and defining environment;
`aqua_invoke_async` runs one as its own execution.

A function that cannot finish at once defers its call (`aqua_defer`): the calling script suspends to its compiled
continuation and resumes when the library resolves or rejects it, which is how every asynchronous Web API is used on
the web. Cancellation is checked at compiled block boundaries, and the host's turns yield during pure computation.

Tests: `compiler/AquariusTests/compiler/CallConventionTest.cs`, compiler/closure language cases and the libraries'
tests on every target.
Measure Wasm emission and execution separately with `AQUARIUS_WASM_BENCHMARKS=1`.
