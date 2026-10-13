# Guarded Wasm fast paths and collection scheduling

The compiler computes candidate slots for parameters and declarations in the
current owned scope. Each direct access checks the scope's current binding count
and the candidate binding's name. Conditional declarations, scope growth, loop
resets, duplicate parameters and host edits can change slot positions; a failed
guard uses ordinary name resolution. Captured outer variables remain dynamically
resolved. This is a guarded slot optimization, not whole-program static typing.

Numeric compound assignment and prefix/postfix increment execute directly in
Wasm for numeric operands. They preserve integer truncation/overflow conversion,
float rounding, left-operand type and prefix/postfix result semantics. Compound
assignment resolves the destination after its RHS completes, including native
callbacks. Strings, invalid operands and other dynamic cases use runtime helpers.

Array reads use guarded array/integer-index paths and return null outside bounds.
Hash/string indexing and unsupported operands retain dynamic handling. Array
writes still use the runtime so aliases, shared backing storage and native dirty
buffer ownership remain intact. Values remain in the 16-byte tagged ABI; full
numeric data-flow specialization, unboxed source locals and inlining are future
work.

`new WasmCompiler(false)` emits the generic paths for differential correctness
tests and diagnostic comparisons. `new WasmCompiler()` enables optimization.
Both compile standard ABI 2 artifacts and preserve the same language behavior.

Desktop execution defaults to `WasmAbi.DesktopQuantum` (65,536 basic blocks).
The existing browser quantum remains 32,768. Embedders may choose a positive
desktop budget with `new WasmRuntime(builtins, engine, quantum)`. The existing
compiler and runtime constructor signatures remain available.

Scheduler yields no longer force a full desktop collection when the guest has no
allocation pressure and no released host leases. The guest retains its 8 MiB
allocation-pressure trigger. Desktop completion and explicit reachability queries
force a trace. Callback reentry defers completion collection until the outer
execution finishes; returned values and callbacks remain pinned.

The additive `aqua_collection_due` and `aqua_collection_generation` exports let
the host distinguish scheduling from tracing. Before releasing native handles,
the host forces a collection and checks that the generation advanced. It never
uses stale liveness bits. Older ABI 2 artifacts lacking these exports retain the
safe eager collection behavior.

The guest collector scans occupied builtin contexts and clears only native IDs
marked by the previous trace. It still traces complete reference graphs, suspended
arguments, pins, cyclic arrays/hashes and dirty native buffers. It is not a
generational or moving collector.

Regression coverage includes the generic/optimized differential suite,
numeric edge cases, conditional scopes, captured loop variables, RHS callback
mutation, deep recursion, capability survival across yields, handle reuse,
retained callbacks, old ABI 2 artifacts, asynchronous browser calls and browser
cancellation. Performance measurements and the before/after diagnostic are in the
sibling AquariusLang_PerformanceBenchmark repository.
