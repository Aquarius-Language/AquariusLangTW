# Generic execution tiers: promoted scopes, register stack, fuel and direct calls

This document describes how the portable core compiler and the Wasm runtime
execute ordinary (generic, dynamically typed) Aquarius functions. Everything here
lives in `AquariusCore` and `AquariusCore/wasm/runtime/runtime.c`. It is plain
WebAssembly with no engine settings, host branches or native code, so the same
artifact behaves identically in Wasmtime (desktop, Android) and browsers.

## Promoted scopes

`WasmScopePlan` builds the lexical scope tree of each body. A scope that no
closure, member function, host projection or dynamic lookup can observe is
*promoted*: its bindings become fixed frame slots instead of a heap scope object
with name/value pairs. Slots are stored at `values[0..locals)` of the frame, below
the operand stack, and start with the `UNDECLARED` tag so conditional declarations,
use-before-declaration errors and shadowing keep their exact semantics.

Functions whose own scope is promoted declare `PromotedScope` and `Locals` in their
definition. `frame()` reserves the slots, so calling such a function allocates no
scope object. Scopes that are captured anywhere keep the original heap
representation; bindings reached through them use the closure-shared binding cache
(`Closure.cache`, invalidated by `cache_era`) instead of a name walk per access.

The plan is bounded (`MaxSlotStates`) so huge bodies fall back to the original
representation rather than spending quadratic compile time.

Dynamic bindings that cannot be promoted also have a small activation-local
address cache. Loads and assignment targets reuse positive binding addresses
across basic blocks and loop backedges. Runtime helpers and calls invalidate all
cached addresses, because declarations can reallocate scope buffers and callbacks
can insert shadowing bindings. Resumption starts with zeroed Wasm locals. Cached
values are always read from their current binding, so mutation remains visible;
the nonmoving collector preserves addresses of reachable scope buffers. This uses
ordinary Wasm locals and requires no additional engine or host ABI.

## Register-resident operand stack

`WasmBodyEmitter` gives every lowered instruction a static operand stack depth and
keeps the top of that stack in Wasm locals (a virtual stack whose entries are in
memory, in a register or both). A dataflow over stack entries and slots proves
known tags, so integers use an untagged `i32` representation with `i64`-checked
arithmetic, guarded division/remainder and saturating conversion; doubles use `f64`.
Up to 32 type-stable, frequently used slots are kept in registers, loaded in the
prologue and written back before any point where the frame can be observed.

Values are spilled to the frame before runtime helper calls, at block edges and
before every resumable exit, so a suspended frame always has the exact generic
layout. A comparison followed by a branch is fused. Branch arms merge their virtual
stack states; values that are only popped are not spilled at edges.

`WasmBodyEmitter` emits every body, at any operand stack depth; `WasmScopePlan` is the one
identifier resolver all bodies use. Unoptimized compilation (the differential tests' reference)
uses a plan that keeps every scope dynamic. If the emitter rejects a promoted plan
(`InvalidOperationException`, reported by `AQUARIUS_WASM_TIER_DIAGNOSTICS=1`), the body is emitted
with the dynamic plan instead.

## Integer shadows of dynamic bindings

A binding the body cannot promote (a module global, or a variable of a captured enclosing scope) that it
updates with `++` or a compound assignment is kept in an `i32` local while it holds an integer. The shadow is
acquired lazily (address lookup plus an `INTEGER` tag test), every access to the name in the body goes through
it, and every barrier (runtime helper, call, return, failure, checkpoint yield and the end of the body) stores
its number back and drops it; the binding's tag stays `INTEGER`, and resumption starts with zeroed locals, so
it simply re-acquires. A plain assignment drops the shadow and stores to memory. Dynamic bindings a body loads
at least twice and never assigns get read shadows: the whole value (tag, ref, number) is cached in locals on the
first load after a barrier, and barriers drop it. Unoptimized compilation, the differential tests' reference,
emits no shadows.

## Execution fuel (`WasmFuel`)

Fuel is consumed only at the body entry and at targets of backward branches; every
cycle passes through one of them. Each checkpoint consumes the longest path, in
generic basic blocks, to the next checkpoint, so a quantum never runs more work
than the original one-check-per-block model, while straight-line code pays nothing.
A checkpoint yields only when the budget is already exhausted and otherwise consumes
with saturation, which cannot livelock when a weight exceeds the quantum. The inline
fast path is `budget > weight && !poll`; `Execution.poll` is armed by the allocator
when a collection is due. Optimized and unoptimized bodies use the same model, so a
given quantum yields at the same points, which the tiered differential tests check.

## Direct callee execution

`rt_call` runs a pushed callee frame immediately on the Wasm stack (`run_callee`)
instead of returning to the scheduler, up to `NATIVE_CALL_DEPTH` (128) nested
activations. Deeper recursion, yields, host awaits and collections still return to
the stackless scheduler, so recursion depth is still limited only by Wasm memory and
cancellation stays observable. Calls into promoted functions bind arguments directly
into slots.

## Typed scalar tier and deoptimization

The typed tier (`WasmIntegerFunction`) specializes closed scalar functions for
integer arguments with any single proven result type (integer, double, float or
Boolean). See [portable numeric compilation](portable-numeric-architecture.md).
Instead of discarding work at its depth bound, when fuel runs out or when a
collection is pending, it *deoptimizes*: native activations are materialized as
generic promoted frames (`rt_numericFrame`, status 4) and the scheduler continues
from exactly that point. Speculation misses are counted per function and disable
fast entry for functions that keep failing.

## Garbage collector

Blocks tile the heap contiguously. The sweep is a single pass by address that clears marks,
coalesces adjacent free blocks into runs, files each run in its size class (a bitmask finds
non-empty classes) and returns a trailing run to the bump pointer. An allocation that misses
its exact class carves the largest free run sequentially before growing the heap. A collection
is due when net allocation (explicit releases subtract) or heap growth (they do not) reaches the
threshold `max(8 MB, min(live bytes, (512 MiB - live bytes) / 4))`. Hosts that opt into weak
mirrors receive dropped exposed arrays and hashes on a released list instead of pinning them. Variable-size `memset` is avoided on hot paths
because engines lower `memory.fill` to an out-of-line call.

## Compatibility

New artifacts declare `functionLayout: 40` (adds `locals` and `flags` to the
function definition). The C# and JavaScript validators accept layouts 24, 32 and 40.
Hosts and runtime packs built before this change reject layout 40, so rebuild them
together with the compiler. The capability import/value ABI stays version 2.

## Platform-specific acceleration

Platform projects may add their own accelerators without changing the core. The
desktop host's `WasmtimeEngine` binds service imports with typed `WrapFunc`
invokers (no boxed argument arrays) and caches the memory export. Its checked
memory spans use a base pointer and length refreshed before host callbacks and
after every guest return, including reentrant and trapping calls. Primitive
reads no longer query Wasmtime for the memory bounds and pointer individually.
Disposal and range checks still apply to every access.

The shared JavaScript adapter reuses a DataView until the memory buffer changes.
Android's JNI adapter caches immutable function signatures and uses stack buffers
for up to eight arguments/results, retaining dynamically sized buffers for larger
signatures. Engine-specific behavior remains inside the platform projects; core
compilation and artifact layout stay portable.

## Tests

`AquariusTests/compiler/WasmTieredExecutionTest.cs` runs differential programs
under the desktop quantum and quanta 1, 3 and 17 and compares results, output and
errors with the unoptimized path; it also checks that closure-free scopes become
frame slots. The JavaScript suite runs the same artifacts in the browser runtime.
`WasmtimeMemoryTest` verifies growth, callbacks, reentry, traps, bounds and disposal.
Android's real-device contract also exercises repeated calls with nine parameters
and nine return values, beyond the stack-buffer fast path.
