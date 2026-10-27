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
wrap around like the generic runtime; division by zero leaves the speculation so the
generic body reports the error, and the minimum integer divided by minus one wraps.
Adapter guards validate both argument tags and payloads.
Uncertain local types or initialization retain the general scalar representation.

Numeric rules remain unchanged, including integer wrap-around, truncating
division, float rounding, left-type-preserving compound assignment, eager Boolean
operations, equality, errors and parameter shadowing. General scalar registers carry
a tag and numeric payload, while proven integer registers omit both repeated type
checks and floating-point normalization. This is a representation selection pass, not a
complete whole-program SSA optimizer or typed-array system.

Scalar code accounts for execution fuel with the shared checkpoint model described in
[generic execution tiers](execution-tiers.md). Functions whose promoted generic body is
available deoptimize instead of failing: at the direct-call depth bound, on fuel
exhaustion or when a collection is pending, typed activations are materialized as
generic frames and the scheduler continues from that point, so no work is repeated.
Integer arguments may now produce integer, double, float or Boolean results.
Functions without such a body keep the earlier restartable speculation: the depth
bound is at most 64, failed speculation restores its original fuel before generic
execution, and a depth failure disables that function's fast entry. Restart is valid
only because the selected body performs no external effects or heap mutation, and no
host reentry occurs during the attempt. Repeated speculation misses are counted per
function and disable fast entry.

This design materializes only typed scalar activations, not arbitrary optimized continuations. Functions with
effects, suspension, uncertain calls or captured data retain resumable execution.
The public `aqua_fN(frame) -> continuation` exports remain available for every
function, including scalar functions.

The compiler resolves candidate bindings through enclosing lexical scopes.
Each intervening scope must remain uncaptured; captured scopes can have been edited
through a retained closure or host projection and use dynamic lookup. Destination
binding count/name guards preserve conditional declarations, host insertion,
callback mutation, shadowing and per-iteration captured scopes.

Artifacts declare `functionLayout: 32` (now 40, see [generic execution tiers](execution-tiers.md)) and optional `numericTable` and
`numericRecursiveName` function metadata. The definition's original 24 bytes are
followed by a scalar adapter table slot and recursive name pointer. Shared desktop
and browser validators reproduce this layout. Existing ABI 2 artifacts without the
layout field retain 24-byte definitions and remain loadable by current hosts. Deploy
new artifacts with the rebuilt hosts supplied by the compiler; the external
capability value representation stays unchanged.

Guest exports `aqua_numeric_fast_calls`, `aqua_numeric_fallback_calls`, and
`aqua_numeric_depth_fallbacks` report successful scalar dispatches, failed attempts
and depth fallbacks; `aqua_numeric_deopts` reports deoptimizations. Direct recursive invocations are not individually counted by
the successful-dispatch counter. Counts are not CPU-time shares.

Differential coverage compares optimized and generic code across numeric types,
nonfinite values, overflow, Boolean operations, errors, conditional locals,
duplicate parameters, rebinding, deep recursion, captures/shadowing and host effects.
A yield-state regression mutates a recursive binding while execution is suspended.
JavaScript WebAssembly tests exercise the same artifact and calling convention,
including fuel exhaustion and old metadata compatibility.

Build the runtime with the pinned LLVM/Clang toolchain and verify its source/binary match before compiling
the compiler. No host-specific numeric implementation, engine setting, native
backend or platform branch is added.
