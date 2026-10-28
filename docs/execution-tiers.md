# Generic execution tiers: promoted scopes, register stack, fuel and direct calls

This document describes how the compiler and the guest runtime
execute ordinary (generic, dynamically typed) Aquarius functions. Everything here
lives in `compiler/AquariusCompiler/wasm` and `runtime/guest/runtime.c`. It is plain
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

## Inline caches of struct fields

A member read (`p.x`) and a member write (`p.x = v`) of generic code check an inline cache before calling the runtime: the
member's name constant has an unused number, which holds the struct type id + 1 and the field index of the last struct
the site's helper saw (`cache_site` in `runtime.c`). A struct whose type has that id reads its field in place; an
unannotated field is also written in place (an annotated field's write still goes through `rt_setMember`, which checks and
widens it). Struct type ids are program-wide and every type value of one declaration has the same fields, so collections
and declarations that run again never invalidate a cache; another receiver (a module, null, another type) takes the
helper, which refills it. The helper itself finds fields through a small cache by (type id, name). Field access in an
untyped loop is three times faster. Module members (`陣列.建立`) are found through the module scope's hash index with the
name's cached hash. Unoptimized compilation, the differential tests' reference, emits no caches (`InlineCacheTest`).

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

## Native tier for fully typed functions

A function literal whose parameters and result are all annotated (`函式(n: 整數): 整數 { ... }`) gets the native tier
(`WasmNativeFunction`) when its body only uses typed numbers and Booleans, typed arrays, arrays of any values (`陣列` /
`array`: their literals are built natively through `rt_nativeArray`, an element read is a whole generic value that may be
passed to an `陣列` parameter after a tag check, and a non-array leaves the call to the callee's generic body, which
reports the annotation error), structs (`結構` / `struct`: a struct-typed value is a `Struct*` in an i32 local, fields
are loaded and stored at fixed offsets with the field's annotation enforced, constructions call `rt_nativeStruct` and
fill the fields in place, a null receiver fails like the generic runtime, and the entry adapter checks a struct
argument's tag and shape), the conversions `整數()`,
`浮點數()`, `雙精度()` and `長度()`, typed bindings of the scope that declares it, and calls to other such functions of
that scope. The promoted generic body's frame slots become typed Wasm locals; integer, float and double operations
follow the generic runtime exactly; loops become Wasm loops; sibling calls are direct Wasm calls passing the outer
bindings' addresses. Untyped locals are typed by the dataflow when every assignment agrees.

The entry adapter (the function's `numeric_table` slot, flag `NATIVE_TIER`) checks every outer binding (its tag, a
callee's closure identity, unshadowed core conversions) and the argument types before any effect, and otherwise
returns to the generic body. Fuel is consumed at the entry and at loop heads with the generic checkpoint weights, the
budget kept in a local between calls. When the budget runs out, a collection is pending or the native recursion depth
bound is reached, the native activations deoptimize through one out-of-line handler into generic frames (`rt_nativeFrame`)
at the same block; the scheduler then yields as usual. A checkpoint's frame is marked with its native site: when the
scheduler runs it again with budget left, `native_resume` calls the resume adapter (the next table slot), which
re-checks the bindings and continues natively at the entry or at that loop head, entering the loop through skip blocks.
A native error (integer division by zero, an out-of-bounds typed array index) is raised with `rt_nativeFailure`. Native
functions return only their result; a signal global carries 5 after an error or the outermost frame after a
deoptimization. `AQUARIUS_WASM_TIER_DIAGNOSTICS=1` reports typed functions the native tier rejects.

Code quality details, all plain Wasm:

- **Static declaredness at deoptimization sites.** A deoptimization writes the slots the scope plan proves declared at
  that point and skips those it proves undeclared; only uncertain slots test their runtime flag. Values that are dead at
  a loop head therefore stay dead, and Cranelift no longer keeps (and spills) copies of them around the loop.
- **Array companions.** Every local that can hold a typed array (slot, operand, cached outer binding) has two companion
  locals, the array's count and values pointer. They are copied with the array and loaded only where an array enters a
  local from memory, so indexing in a loop is a compare against a register and one load. Native code never resizes
  an array; after a copy-on-write (`rt_nativeWritable`) or a call to a function that can write, the values pointers
  are read again.
- **Exact integer payloads.** Integer and Boolean payloads are always exact 32-bit integers, so typed loads convert
  them with `f64.add 1.5·2^52` and a reinterpretation instead of a saturating conversion and its branches.
- **Strings.** 字串 / string values are a `Text*` in an i32 local, never null. `長度`, code-unit reads (`s[i]`, a
  one-unit text) and `==` / `!=` run in place or through tiny helpers (`rt_nativeUnitText`, `rt_nativeTextEqual`);
  `+` calls `rt_nativeConcat`. `s = s + t` on a string slot appends in place (`rt_nativeAppend`): the text it builds is
  marked as owned by the slot in the pointer's low bit, and every other load of the slot and every frame it is written
  to drops the mark, so no other value can see the text grow. A read past the end deoptimizes at that read (it starts a
  generic block), where the generic code gives null. A function that allocates (strings, array literals, structs,
  directly or through callees) also polls for a pending collection at its loop heads. Typed string work runs about 15×
  faster than generically (`NativeStringTest` compares it with the unoptimized compiler).
- **Core globals.** `長度` / `len` and the conversions are found in the host's builtin scope, never in a closure's scope
  chain. So an entry adapter or region accepts them when no binding of that name exists, and checks a shadowing binding
  for the very builtin. Before this round, any typed function or region that used them always fell back to the generic
  body at run time.
- **Tail calls.** A call whose result the function returns at once (`回傳 f(...)`, with the same result type) is a Wasm
  `return_call` (the tail-call proposal, on by default in Wasmtime and in current browsers): the callee takes over the
  activation and keeps its depth, so tail recursion runs natively at any depth. When the callee deoptimizes, its generic
  frame's parent is the tail caller's caller, which is where the tail caller's frame would have returned the result.
- **Pure calls.** A call to a function that (transitively) assigns no outer binding and writes no array element does
  not reload outer bindings or array companions afterwards.
- **Inlining.** A small native leaf (no calls, loops or outer bindings, nothing it can mutate) is emitted in place of
  its calls. The caller tests the callee's entry checkpoint first (depth bound and budget); where it would deoptimize,
  the out-of-line handler builds the callee's generic frame at its entry and the caller's frame after the call, exactly
  what the call would have left, so the loop around an inlined call contains no call and keeps its values in registers.
  The budget consumed is the callee's entry weight, as before.
- **Collections are polled by the adapters.** Native code only allocates when it leaves (a deoptimization) or copies an
  array on write, so the entry checkpoint no longer reads `Execution.poll`; the entry adapter sends a call made while a
  collection is pending to the generic body, whose entry checkpoint collects.

## Typed clones of untyped functions (every target)

An untyped function without any annotation whose parameters all have guesses (the static or guessed types of the
arguments of its direct calls, see below) is also a native-tier candidate: a *clone* (`NativeSignature.Clone`) typed by
those guesses. The native tier proves its result type by trying integer, double, Boolean, float, an array of any values
and no value in turn (callers use the callee's current choice, so mutually recursive clones settle together); a clone
whose body cannot be typed stays generic. Unlike a typed function, a clone's entry adapter takes only arguments of exactly
its types (an integer is not widened to a double parameter) and arrays of only its element type (`rt_arrayKind`), and a
call between native functions passes a clone exactly those types, so its generic semantics never change; any other call
runs the generic body. Untyped outer bindings it uses are speculated on their guesses and checked by the entry adapter,
guessed arrays are loose arrays, and a read outside one deoptimizes to the generic frame at that read (which starts a
generic block). A clone takes precedence over the integer tier and over regions of its loops. Results of untyped functions are guessed too (the
values of their return statements), so `變數 樹 = 建立(n)` passes an array to `檢查(樹, n)`; arrays of different element
types merge to an array of any values. The untyped Ackermann benchmark runs 5× faster, the spectral norm 12× and the binary
trees 2×, on every target. `CloneTest` compares clones with the unoptimized
compiler: guesses that fail, every result type, outer writes, reads past the end, errors, arity, rebinding and tiny quanta.

## Native regions in untyped code

A loop of an untyped body (a top-level script, an untyped function whose loop scopes are promoted, or a typed function
the native tier rejects, for example because it calls `陣列.建立`) whose code only uses typed bindings of any enclosing
scope (`變數 總和: 整數 = 0;` before the loop), its own loop variables, typed arrays (including typed array locals declared
before the loop) and the core conversions becomes a native region (`WasmNativeFunction.SelectRegions`): the same native code, entered at
its loop heads instead of a function entry. At every loop-head block of the region, the generic body writes its
registers back and calls the region with the frame; the region resolves its outer bindings from the frame's scope, checks
their types and the types of the frame slots it uses, runs from that loop head, and returns the generic block to continue
at: the loop's exit, or the loop head where the budget ran out, whose generic checkpoint then yields (a later resumption
enters the region again). A region that cannot run in an activation (a binding of another type) returns -2 and the
generic body skips it until the next resumption; a raised error returns -3. Regions make no calls, so their state is only
the frame's slots and the outer bindings.

### Speculation on untyped variables (every target)

Untyped variables a loop uses also make regions, typed by a *guess*: the TypeChecker records the static type of every
value a single declaration and its plain assignments give a variable (`變數 n = 60;` is an integer), and of every argument
of the direct calls of a function for its untyped parameters; disagreeing values give no guess. A region assumes each
untyped variable it uses (an outer binding or a frame slot declared before the loop) holds its guessed number or Boolean
type. Since the region checks those types at every entry, a wrong guess only returns -2 and the loop runs generically,
so speculation never changes a result (`SpeculationTest` compares it with the unoptimized compiler, also when a
variable changes type between entries and under tiny quanta). This is plain Wasm in the artifact, so web and Android
gain too: the untyped mandelbrot benchmark runs as fast as the typed one.

### Speculation on untyped arrays (every target)

The TypeChecker also guesses arrays: `陣列.建立(n, 0)` / `Array.Make(n, 0.0d)` (a scalar fill), an array literal of scalars
of one type, arithmetic of guessed numbers, and values copied from guessed variables or read from guessed arrays (`變數
pivot = a[i]`, parameters from the arguments of direct calls; these derived guesses are resolved after the whole program,
to a fixpoint). A region speculates that such an untyped array holds only elements of its guessed type (a *loose* array,
`WasmNativeFunction.LooseFlag`): it reads and writes it like the typed array, and writes only values of exactly that type.

Every untyped array keeps its element kind (`Array.kind`: the tag all its elements have, or 0 when unknown): writes of
another tag, host access to its values and library functions that mix elements reset it, `建立`, `填入`, literals and
copies set it. A region's entry calls `rt_arrayKind`, which accepts a typed array of that element, an untyped array of that
kind, or (when the kind is unknown) scans it once and records the kind; host-exposed arrays are scanned at every entry.
A read outside a loose array cannot be the typed tier's error (the generic code gives null): every such read starts a
generic block (`WasmBodyEmitter.BlockStarts`), and the region leaves at it with its slots and operands written to the frame,
so the generic code performs the read. The untyped matrix benchmark runs 9× faster, the sieve and quicksort about 2×, on
desktop, web and Android alike. `LooseArrayTest` compares them with the unoptimized compiler: reads past the end, element
types changing between entries, library changes and tiny quanta.

### Desktop runtime JIT

Variables whose type no guess finds (values from untyped calls, parameters of callbacks) are left to the desktop
runtime JIT, a copy-and-patch JIT. For each loop of untyped code that no region covers, the compiler stores typed
variants (stencils) in the `aquarius.jit` custom section (`WasmJit`, `WasmNativeFunction.SelectStencils`): the loop's
region under every assignment of integer or double to its untyped scalar variables (up to three), most likely first.
Each stencil is region code emitted against its own imports: the runtime helpers it calls, the application's memory and
table, and the native tier's signal and execution globals, which the application exports as `aqua_signal` and
`aqua_execution`. Other engines ignore the section.

The generic code of such a loop head (`WasmBodyEmitter.EnterJit`) keeps two globals: the table index of the loop's
installed variant (0 while cold, -1 when none applies) and a count of cold iterations. After
`WasmBodyEmitter.JitThreshold` iterations it calls `rt_jitRequest`, a host service the Wasmtime engine answers through
`platform_jit`. On Windows and Linux, `runtime/platform/desktop/jit.c` builds a module around the loop's next variant
(the application's own type section, the imports, the stencil as function `run`), and `engine_install` compiles it with
the caching of applications (`wasmtime-jit-<slot>.bin`), instantiates it in the requesting store (the main instance or a
Thread worker's) and appends it to the function table. Because regions enter at loop heads, a long loop switches to
native code in the middle of its run. A variant whose entry check rejects the values returns -2 and the loop asks for
the next variant, until none is left (-1). Android and the web answer -1, so their loops stay generic.

`AQUARIUS_JIT=0` turns the JIT off; `AQUARIUS_JIT_DIAGNOSTICS=1` reports each installation and how long it took (a few
milliseconds, a fraction of one once cached). `JitTest` runs untyped programs with the JIT on and off (and against the
unoptimized compiler): type changes between and inside runs, entry checks that fail, errors raised in native code,
collections during native code, Thread workers and tiny quanta.

## Garbage collector

Blocks tile the heap contiguously. The sweep is a single pass by address that clears marks,
coalesces adjacent free blocks into runs, files each run in its size class (a bitmask finds
non-empty classes) and returns a trailing run to the bump pointer. An allocation that misses
its exact class carves the largest free run sequentially before growing the heap. A collection
is due when net allocation (explicit releases subtract) or heap growth (they do not) reaches the
threshold `max(8 MB, min(live bytes, (2 GiB - live bytes) / 4))`. Hosts that opt into weak
mirrors receive dropped exposed arrays and hashes on a released list instead of pinning them. Variable-size `memset` is avoided on hot paths
because engines lower `memory.fill` to an out-of-line call.

## Compatibility

Artifacts declare function layout 40 (adds `locals` and `flags` to the function definition), the only layout the
runtime (`runtime/host/application.c`) accepts. The capability import/value ABI stays version 2.

## Host calls

The native host (`runtime/host/engine_wasmtime.c`) binds the service import once with Wasmtime's unchecked function
API and reads guest memory through the memory export's base pointer, refreshed after every guest return. The web host
is a side module in the application's own memory, so it reads guest values directly. Engine-specific code stays in
`engine_*.c`; compilation and artifact layout are the same for every target.

## Target-specific tuning

Every target runs the same artifact; a target may tune only its engine or platform layer (CLAUDE.md rule 11). The
desktop host (`runtime/platform/desktop/main.c`) runs executions in run slices of 2^20 fuel instead of the portable
65536: a desktop process shares no UI thread with a browser and has no Android input watchdog, and hot typed loops
then leave native code at a slice's end (deoptimize, yield, re-enter) 16 times less often. The quantum never changes
results; `AQUARIUS_FUEL_QUANTUM` overrides it, and `NativeFunctionTest.TheDesktopQuantumGivesThePortableResults`
compares the desktop, portable and a tiny quantum. Web and Android keep the portable quantum.

## Startup

Wasmtime compiles an application in tens of milliseconds, so the native host keeps the compiled code between runs:
`wasmtime-<slot>.bin` files under `%LOCALAPPDATA%\Aquarius\cache` (Windows), `$XDG_CACHE_HOME/aquarius` or
`~/.cache/aquarius` (Linux) and the app's cache directory (Android). Each holds one application's SHA-256 and its
serialized code; 32 slots bound the cache, and a run whose slot holds another application compiles and takes it.
Deleting the folder is always safe. Process start itself is kept small: the launcher reads only the end of its
executable (where `aqua build` embeds an application), and `opengl32.dll`, which only wgpu's GL fallback uses, is
delay-loaded. An empty program starts and exits in about 20 ms on Windows with a warm cache.

## Tests

`compiler/AquariusTests/compiler/WasmTieredExecutionTest.cs` runs differential programs
under the runtime's quantum and quanta 1, 3 and 17 and compares results, output and
errors with the unoptimized path; it also checks that closure-free scopes become
frame slots. `WasmtimeMemoryTest` verifies growth, callbacks, reentry, traps, bounds and disposal.