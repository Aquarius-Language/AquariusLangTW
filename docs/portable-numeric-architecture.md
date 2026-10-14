# Portable scalar compilation and lexical binding resolution

The compiler selects an internal execution representation for closed scalar
functions. Numeric and Boolean parameters, source locals and temporary results
use Wasm registers instead of the guest's tagged operand stack. Known self-calls
use a direct typed Wasm signature. Successful scalar calls allocate no lexical
scope or language frame. The capability import/value ABI remains version 2.

Eligibility is conservative: at most eight parameters and 256 lowered instructions;
forward control flow; primitive constants and locally owned bindings; no loops,
heap objects, captured writes, foreign calls, computed members or escaping closures.
Calls may only use one captured recursive binding, whose closure identity is checked
before entering scalar code. Unknown argument types, uninitialized locals and invalid
operations retain generic execution.

An additional control-flow/type proof specializes functions whose integer arguments
produce integer returns on every terminating path. Their values use untagged `i32`
locals and direct `i32` recursive calls. Addition, subtraction and multiplication
use a checked `i64` intermediate; division guards zero and minimum-integer divided
by minus one. Out-of-range results preserve Aquarius's minimum-integer conversion
rather than wrapping. Adapter guards validate both argument tags and payloads.
Uncertain local types or initialization retain the general scalar representation.

Numeric rules remain unchanged, including integer overflow conversion, truncating
division, float rounding, left-type-preserving compound assignment, eager Boolean
operations, equality, errors and parameter shadowing. General scalar registers carry
a tag and numeric payload, while proven integer registers omit both repeated type
checks and floating-point normalization. This is a representation selection pass, not a
complete whole-program SSA optimizer or typed-array system.

Scalar basic blocks account for execution fuel. The direct-call depth bound is at
most 64 and decreases with register pressure. Fuel and depth failures return to
the existing stackless scheduler. Failed speculation restores its original fuel
before generic execution: discarded work must not trigger an observable yield with
restarted call state. Restart is valid only because the selected body performs no
external effects or heap mutation, and no host reentry occurs during the attempt.
A depth failure disables that function's fast entry for the rest of the execution,
avoiding repeated deep speculative traversals.
Recursive fast entry requires the integer proof and integer arguments. General
tag/payload recursion is deliberately kept on the resumable path: measured repeated
speculation and normalization cost outweighed its allocation savings.

This design does not materialize arbitrary optimized continuations. Functions with
effects, suspension, uncertain calls or captured data retain resumable execution.
The public `aqua_fN(frame) -> continuation` exports remain available for every
function, including scalar functions.

The compiler resolves candidate bindings through enclosing lexical scopes.
Each intervening scope must remain uncaptured; captured scopes can have been edited
through a retained closure or host projection and use dynamic lookup. Destination
binding count/name guards preserve conditional declarations, host insertion,
callback mutation, shadowing and per-iteration captured scopes.

New artifacts declare `functionLayout: 32` and optional `numericTable` and
`numericRecursiveName` function metadata. The definition's original 24 bytes are
followed by a scalar adapter table slot and recursive name pointer. Shared desktop
and browser validators reproduce this layout. Existing ABI 2 artifacts without the
layout field retain 24-byte definitions and remain loadable by current hosts. Deploy
new artifacts with the rebuilt hosts supplied by the compiler; the external
capability value representation stays unchanged.

Guest exports `aqua_numeric_fast_calls`, `aqua_numeric_fallback_calls`, and
`aqua_numeric_depth_fallbacks` report successful scalar dispatches, failed attempts
and depth fallbacks. Direct recursive invocations are not individually counted by
the successful-dispatch counter. Counts are not CPU-time shares.

Differential coverage compares optimized and generic code across numeric types,
nonfinite values, overflow, Boolean operations, errors, conditional locals,
duplicate parameters, rebinding, deep recursion, captures/shadowing and host effects.
A yield-state regression mutates a recursive binding while execution is suspended.
JavaScript WebAssembly tests exercise the same artifact and calling convention,
including fuel exhaustion and old metadata compatibility.

Build the runtime with pinned Zig and verify its source/binary match before compiling
the compiler. No host-specific numeric implementation, engine setting, native
backend or platform branch is added.
