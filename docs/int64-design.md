# Design: a 64-bit integer type (長整數 / long)

Status: proposal only. Nothing here is implemented. It records what a 64-bit integer touches in the current
architecture and recommends a plan, so the work can be scheduled and split.

## Why

Integers are 32-bit (`整數` / `int`, wrapping like `i32`). Programs that need more than ±2^31 use doubles, which are
exact only up to 2^53 and give no wrap-around or bit operations above 32 bits. Concrete needs: timestamps in
milliseconds or microseconds, file sizes and offsets above 2 GiB, 64-bit hashes (FNV-1a 64, xxHash, SipHash) and
PRNGs (xoshiro256), IDs from JSON APIs and databases, and exact integer arithmetic for money in minor units. WebAssembly
has native `i64`, so the cost in compiled code is small; the cost is in the value representation and the ABI.

Non-goals: arbitrary-precision integers (a BigInt library can come later as an Aquarius/C library), unsigned types,
128-bit integers.

## Language surface (recommended)

- Type name `長整數` / `long` in annotations and as a conversion `長整數(x)` / `long(x)`; arrays `長整數[]`.
- Literals: a suffix `L` (`5000000000L`, `0xFFFF_FFFF_FFFF_FFFFL` = -1, `0b1L`). Without the suffix an integer literal
  stays 32-bit and out-of-range literals stay compile errors, so no existing program changes meaning.
- Arithmetic: `int op long` gives `long`; `long op float` and `long op double` give `double` (a float cannot even hold
  25 bits; C# would give float here — deliberately not followed). `+ - *` and negation wrap (two's complement, like
  `i64.add`); `/` and `%` truncate, division by zero is an error, the minimum divided by -1 wraps to itself, exactly
  the 32-bit rules widened.
- Bitwise operators (`& | ^ ~ << >> >>>`) work on longs; shift counts use their low 6 bits (`i64.shl`). `int op long`
  widens the int first.
- Conversions: `長整數(x)` truncates floats toward zero and saturates (NaN gives 0), like `整數(x)`. `整數(long)`
  saturates too, so `整數(x)` keeps one rule; wrapping narrowings are `整數(x & 0xFFFFFFFFL)`-style or a later
  `Math.Wrap32`. `雙精度(long)` rounds to nearest.
- Comparisons and `==` between long and int are exact; between long and double they compare the exact values (not the
  rounded double), so `9007199254740993L == 9007199254740992.0d` is false.
- Printing: plain decimal (`9223372036854775807`), never exponent notation. `String.From` and 印出 agree (decimal.h
  gains `format_long`). `String.ParseNumber` keeps returning int or double; a new `String.ParseLong` returns a long or
  null.
- `數學` / `Math`: Abs, Sign, Min, Max, Clamp keep long; the rounding functions are identity; `*Int` variants stay int;
  transcendental functions take long as double. `Math.RandomLong(low, high)` for 64-bit ranges.
- Hash keys: a long key is distinct from an int key of the same value (keys are typed, as int and double are today).

## What it touches

### Value representation (guest, `runtime/guest`)

`Value` is `{ u32 tag, ref; double number; }` (16 bytes, `layout.def`). A long fits the 8-byte payload as its bit
pattern: `union { double number; int64_t integer; }`. No size change, no GC change (longs hold no references).

The hard part is the tag. Today the numeric tags are contiguous and ordered by widening (`INT=1, FLOAT=2, DOUBLE=3`):
`numeric(v)` is a range test, and the result type of arithmetic is `max(tag_a, tag_b)` in `runtime.c` (`binary`), the
generic emitter, the native tier and the TypeChecker. Annotation codes keep the element type in 4 bits (`code & 15`,
`+16` arrays, `+32` structs).

- Option A (recommended): a new tag `LONG = 17` after `STRUCT_TYPE`, no renumbering. `numeric()` becomes a bit test
  (`(1u << tag) & NUMERIC_TAGS`) and widening becomes a small rank table (`int < long < double`, float with long →
  double) in one helper per component (`runtime.c` `widen()`, C# `NumericTypes.Widen`). Annotation code: use the free
  nibble value 6 for long (`code & 15 == 6`) and map it to tag 17 in the two places that compare codes with tags
  (`rt_checkType`, `fits`, and their emitter twins). Existing compiled applications, hosts and libraries keep working
  because no existing number changes.
- Option B: renumber (`INT=1, LONG=2, FLOAT=3, DOUBLE=4`, shifting every tag). Keeps `max(tag)` but breaks the guest
  ABI, the SDK enum, every host and every compiled `.wasm` (metadata ABI 4 → 5). Not worth it.

Every guest site that reads `v.number` for a numeric value must look at the tag first: `binary`, `equal`, `key_hash`,
`number()`/`saturate_int`, increments (`successor`), `fits`/`element_fits`, packed array load/store, array kernels
(`Sum`, `Min`, `Max`, `Sort`: an int64 introsort beside the int32 one), `format_value`, the thread packet encoder, and
`maths.h`. A grep for `numeric(` and `.number` gives the list (about 130 occurrences in the guest today).

### Packed arrays

`ArrayStorage.layout` gains `LONG` with 8-byte elements (`layout_size`), `load_element`/`store_element`/
`fill_elements` cases, and `lock_array` for `長整數[]`. Integers widen into a `長整數[]` like ints into `雙精度[]`.
Hosts that read packed arrays (`values.c`, `aqua_array_*` in the SDK, web glue array views) need the new layout.

### SDK and hosts (`runtime/sdk`, `runtime/host`, `web-glue`)

- `aqua.h`: `AQUA_LONG = 17`, `aqua_value` gets the anonymous union (`double number; int64_t integer;`) — source
  compatible —, `aqua_long(int64_t)`, `aqua_arg_long`, `aqua_return_long`, `aqua_is_number` includes long, and
  array element access for long arrays. Raise `AQUA_API_VERSION` to 3; libraries built against 2 keep loading because
  nothing they use changes. Append the new functions to `api.def` before the platform block.
- `format.c`: `format_number` prints longs exactly (shared with the guest through `decimal.h`).
- `values.c`, `session.c`, `threads.c`, `promise.c`: wherever a value's number is converted (JSON-like structured
  clone between threads, promise results) handle the tag.
- `web-glue`: any glue that reads a `Value` from memory as `Float64` must use a `BigInt64Array` view for tag 17 (the
  network library's JSON and WebSocket glue pass strings, so few domains are affected; audit with a grep for
  `Float64Array` and `getFloat64`).
- Libraries: `aqua_arg_number` (double) keeps working for longs (converted, possibly inexact); libraries that want
  exact 64-bit values (Network JSON, Terminal file sizes, Audio sample positions) opt in.

### Compiler (`compiler/AquariusCompiler`)

- Lexer/parser: the `L` suffix (decimal, hex, binary), `LongLiteral`, `LongObj` constants (pool constants are 16-byte
  Values already; the number slot holds the bits), the type name and conversion builtin.
- TypeChecker: the `Long` type code, the widening table, `Combines`, annotation checks, guesses.
- Lowering/IR: no new operations — the existing arithmetic, comparison and bitwise operations become type-generic.
- `WasmScopePlan` tags, `WasmBodyEmitter`: unboxed `i64` registers for proven longs (`I64` locals beside the existing
  `I` and `N` ones), inline `i64` arithmetic with the same checked division as ints, helper calls otherwise.
- Native tier: a `LongType` with `i64` locals, direct `i64` ops, packed `長整數[]` element access, deoptimization
  writing the bit pattern into the frame slot, entry checks of long arguments. The typed scalar tier
  (`WasmIntegerFunction`) can stay int-only at first (it rejects anything else).
- The desktop JIT stencils inherit the native tier's code; no change in `jit.c` beyond what the encoder needs for new
  opcodes (it re-encodes stencil bodies, so check its opcode tables for the `i64` instructions).
- Language server: type name, hover text, completion for `長整數`; VS Code grammar for the `L` suffix.

## Recommended plan

1. **Runtime core (guest + host formatting):** tag 17, `numeric()` as a bit test, the widening helper, `binary`,
   `equal`, `key_hash`, conversions, increments, printing (`decimal.h`), `String.ParseLong`, thread packets. Tests:
   shared JSON vectors (`tests/long-integer.json`) for wrap-around, division, comparisons with doubles beyond 2^53,
   hashing and printing. Ship behind no flag: nothing creates a long until the compiler can.
2. **Compiler front end and generic emitter:** literals, type name, conversion, TypeChecker, inline `i64` fast paths
   for proven longs. Differential tests (generic vs optimized) like `FloatRemainderTest`/`BitwiseOperatorTest`.
3. **Packed `長整數[]` and the SDK (API 3):** layout, hosts, web glue views, `aqua_long` accessors; library tests on
   every target.
4. **Native tier:** `i64` locals, arrays, calls, deoptimization; native-acceptance and differential tests.
5. **Math and libraries:** Math functions for long, `RandomLong`; Network JSON exact integers; docs.

Each phase is one commit series that keeps every existing test and expected output unchanged (no program can produce
a long before phase 2). Phases 1–2 are the minimum useful release; 3–4 make it fast; 5 rounds it off. Rough size:
phase 1 ~400 lines of C, phase 2 ~600 lines of C#, phase 3 ~300 lines across hosts/SDK/glue, phase 4 ~500 lines of C#.

## Risks

- Every `.number` read that forgets the tag silently mis-reads a long as a denormal double: phase 1 should add a debug
  assertion build of the guest (or a test that runs every portable module function with long arguments).
- Performance of untyped code: `numeric()` and widening get slightly more expensive; the emitter's proven-type fast
  paths are unaffected. Measure with the existing benchmarks (`AQUARIUS_WASM_BENCHMARKS=1`).
- JSON and the web: JavaScript numbers cannot hold longs; the web glue must pass them as BigInt or decimal text.
