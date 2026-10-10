# Aquarius Wasm value ABI 2

ABI 2 applications link the portable runtime from `AquariusCore/wasm/runtime`.
Recompile ABI 1 artifacts. Both desktop and browser hosts execute the same runtime
binary; they provide capability transport rather than language execution.

This is an implemented application ABI. It is **not** the pending general-purpose
third-party library ABI or a Wasm Component Model interface.

## Values and memory

The runtime uses wasm32 linear memory. A value occupies 16 bytes, aligned to eight:

| Offset | Representation | Meaning |
| --- | --- | --- |
| 0 | little-endian u32 | tag |
| 4 | little-endian u32 | guest reference, native handle or portable builtin ID |
| 8 | little-endian f64 | numeric payload |

| Tag | Meaning |
| --- | --- |
| 0 | no result |
| 1 / 2 / 3 | signed 32-bit integer / single-precision float / double |
| 4 | boolean, numeric payload zero or one |
| 5 / 6 / 7 | string / array / hash |
| 8 / 9 / 10 | guest closure / guest module scope / native handle |
| 11 / 12 / 13 / 14 | null / break / language error / portable builtin |

Text stores a u32 UTF-16 code-unit count followed by that many little-endian u16
units. Public length preserves UTF-16 indexing. An integer arithmetic result is
computed through f64 and truncated when within the signed integer range; NaN,
infinity and out-of-range results become `INT_MIN`. Float results round to f32.
Ordinary arithmetic promotes to the wider numeric tag; compound assignment
preserves the left operand's tag. Boolean operators remain eager.

Hash lookup compares complete typed keys, resolving accidental host-hash
collisions. Integer, float and double keys remain distinct. Equal signed zeros
share a key; NaNs of the same numeric type are equal for hash lookup, while a
numeric NaN still compares unequal in language expressions.

Application data begins at 4 MiB. It contains function definitions, parameter
names and constant pools. The heap starts after this data; memory can grow up to
512 MiB. Object references are guest addresses; native handles are session-local
indices, never pointers into managed or JavaScript memory.

## Calls, scheduling and collection

Compiled functions take a guest frame address and return a continuation index.
The emitter uses Wasm arithmetic and guarded dynamic fallbacks in the guest.
Stack capacity is derived from reachable operand depth, including branches,
calls and loop exits. The guest scheduler creates child frames and handles
returns without recursively consuming the host stack.

The collector is nonmoving. It traces executions, live operands, suspended call
arguments, closures, scopes, collections, explicit pins and pending dirty buffers.
Small scopes use direct binding search; larger scopes use collision-safe indexes.
Loop frames cache binding addresses behind a scope generation guard; declarations,
deletion and loop scope changes invalidate those addresses, including during reentry.
Released frames and scopes are reused through size-class free lists. Collection
runs at safe guest checkpoints after allocation pressure and at scheduler yields.
Reported heap bytes are a high-water mark, not live or retained memory.

`push` returns a distinct array header and can share backing storage. Guest writes
and host writable-buffer access detach shared storage first. Mutable aliases of
the same array retain identity; independently appended arrays retain snapshots.
The collector accounts for backing storage references when headers die.

## Capability transport

The only host import is:

```text
aquarius_v2.service(operation, context, subject, name, arguments, count, output)
    : (i32, i32, i32, i32, i32, i32, i32) -> i32
```

| Operation | Meaning |
| --- | --- |
| 0 | resolve an available capability or portable builtin |
| 1 | invoke a native capability |
| 2 | resolve a member of a native service module |
| 3 | project a native module scope for computed member lookup |

Arguments and output use the value layout above. A pending browser capability
returns one; the host retains its arguments until completion and resumes the
guest execution with `aqua_resume`. Cancellation, errors and callback reentry
retain separate execution frames. Desktop pending I/O remains migration work.

Hosts pin values retained outside Wasm and release pins after their owners die.
Guest arrays exposed to native services enter a dirty queue on mutation. Known
service owners receive their own updated buffers; arbitrary embedding callbacks
synchronize their visible scopes. Strings are copied at the boundary; bulk buffer,
resource ownership and general library linking contracts remain to be implemented.

Desktop `WasmRuntime.Dispose()` releases owned native stores. Retained callbacks
cannot be invoked after disposal; disposal during callback reentry is rejected.
Packaged applications own one runtime and release it after native service shutdown.
Standalone compile-and-run convenience APIs still rely on owner/GC lifetimes.

## Build and compatibility

Build with pinned Zig 0.15.1 using `scripts/build-wasm-runtime.ps1`. `-Check`
rebuilds and compares the checked-in binary. The runtime uses bulk-memory
instructions (`memory.copy`/`memory.fill`); a supporting engine is required.
The desktop Wasmtime cache owns machine-code cache validation and keys. Portable
Wasm remains authoritative. First compilation, process startup with a populated
cache and warmed execution are different measurements.

See [migration progress](wasm-migration-progress.md) and the
[complete backlog](wasm-architecture-migration.md) for remaining library,
toolchain, native launcher and cross-platform requirements.
