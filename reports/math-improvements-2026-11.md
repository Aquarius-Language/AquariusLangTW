# Math improvements, November 2026

Branch `math/core-improvements` (from `main` 036e9b4), built and tested in the worktree
`C:\OfficialProjects\AquariusLangTW-math` with its own `.native-build` packs (the pinned toolchain and SDKs are shared
read-only). Implements the math-handling review of 2026-11-02, step by step, one commit (or two) per step. Not merged.

## Commits

| Commit | Step |
| --- | --- |
| `2d41d8b` | 1. Portable `數學` / `Math` module in the guest runtime, vendored musl libm subset |
| `ea7d9a3` | 2. Native tier compiles Math calls and constants |
| `2598a95` | 2. Fix: `Math.Random()` (no arguments) no longer stops the native analysis |
| `e68bbab` | 3. Number literals; `String.ParseNumber` |
| `b25f8ea` | 4. Float `%` is the exact IEEE remainder in every tier |
| `0b11215` | 5. One number text for `印出` and `String.From`, shared exact digit algorithm |
| `3cf94d0` | 6. Integer bitwise operators |
| `3e58070` | 8. Processing / GLM math on musl; Models and the GL test helpers call Math |
| `ffd8722` | 9. `docs/int64-design.md` (design only) |
| `d504ff6` | `examples/math_showcase` |
| (this report) | CHANGELOG (Unreleased) and this report |

Step 7 (seeded portable RNG) was delivered inside step 1: `Math.Random`, `Math.RandomInt`, `Math.Seed` (PCG32).

## Before (measured on main + the new Math module only, before steps 3–6)

| Probe | Before |
| --- | --- |
| `100000000000000000.0d % 3`, `% 3.0d` | 0, 0 (exact: 1) |
| `-0.0d % 3`, `5.0d % Infinity` | 0, NaN (exact: -0, 5) |
| `-7.5d % 2`, `7.5d % -2`, `1.5f % 0.4f` | -1.5, 1.5, 0.29999998 (correct) |
| `印出(-0.0d)` / `字串.轉文字(-0.0d)` | `-0` / `0` |
| `印出(1e17d)` / `轉文字` | `1E+17` / `100000000000000000` |
| `印出(0.00001d)` / `轉文字` | `1E-05` / `0.00001` |
| `印出(1e21d)` / `轉文字` | `1E+21` / `1e+21` |
| `印出([1.5,-0,1e-7])` / `轉文字` | `[1.5, -0, 1E-07]` / `[1.5, 0, 1e-7]` |
| float ties, e.g. `3047523.25f` | 印出 `3047523.2`, 轉文字 `3047523.3` |
| `1.5` | lexical error (`ILLEGAL "1.5)"`) |
| `1e-9`, `0xFF`, `1_000`, `2d`, `2f` | parse errors (a number followed by a name) |
| `6 & 3`, `6 \| 3`, `6 ^ 3`, `1 << 3`, `~1` | parse errors (`&`/`\|` were TODO branches in the lexer) |

## What changed, per step

### 1. `數學` / `Math` (runtime/guest/maths.h, builtin ids 60..96)

- 37 functions and 5 constants, bilingual (Chinese names follow Processing's: 平方根, 正弦, 次方, 絕對值, 向下取整, 四捨五入, ...):
  Abs, Sign, Min, Max, Clamp (keep the widest argument type), Floor, Ceil, Round, Trunc (same float type, integers
  unchanged) and FloorInt/CeilInt/RoundInt/TruncInt (saturating like `整數()`), Sqrt, Cbrt, Hypot, Pow, Exp, Log, Log2,
  Log10, Sin, Cos, Tan, Asin, Acos, Atan, Atan2, Sinh, Cosh, Tanh, IsNaN, IsFinite, Fmod, Random, RandomInt, Seed;
  PI, E, TAU, Infinity, NaN.
- IEEE results, no domain errors (`Log(0)` = -Infinity, `Sqrt(-1)` = NaN). Round = half away from zero (documented;
  not banker's, not JS's half-up). Transcendental results are double, or float for float arguments.
- musl 1.2.5 libm subset vendored in `runtime/guest/third_party/musl` (tarball SHA-256 pinned, MIT, files unchanged,
  shim headers for the generated ones) and compiled beside `runtime.c` into `runtime.wasm`; sqrt/floor/ceil/trunc/min/
  max/abs are Wasm instructions. Results are bit-identical on every target by construction.
- PCG32 RNG: `Seed(n)` gives the same sequence everywhere (pinned in `tests/math-library.json` and checked against an
  independent Python reference); unseeded, it seeds once from the host clock. Processing's `System.Random` clone is
  unchanged.
- Build scripts compile the musl sources and export `rt_*` of every guest header (also `double` results); language
  server facts (functions + constants), tests that keep the tables equal, `docs/math.md`.
- Cost: `runtime.wasm` 161,738 → 200,984 bytes for the module (210,572 at the end of the branch, with ParseNumber,
  the shared formatter and the bitwise helpers); every application grows by about 39 KB (musl code and its
  exp/log/pow tables), 48 KB in total. If that matters for the web, the table-driven exp/log/log2/pow could be replaced by musl's older table-free
  versions (about 14 KB less).

### 2. Math in compiled code

- `WasmMath.cs` (new) lists the module as maths.h declares it (a test keeps them equal). The native tier
  (`WasmNativeFunction.MathCall`) compiles calls with number arguments: integer Abs/Sign/Min/Max/Clamp as i32
  operations, Sqrt/Floor/Ceil/Trunc/Abs/Min/Max as the f64 instructions, the rest through the guest's exported
  `rt_math`, so native results equal the generic call's exactly. Constants (`Math.PI`, ...) compile to f64 constants.
  The entry and resume adapters check that no binding shadows the module (like the core conversions). Random,
  RandomInt and Seed stay generic.
- The closed typed scalar tier (`WasmIntegerFunction`) accepts no builtin calls at all today, so it does not take Math
  either; typed functions go through the native tier and untyped ones through native clones, which cover the use.
- Tests: `NativeMathTest` (differential generic vs optimized with NaN, ±0, ±Infinity, float vs double, integer
  wrap-around, clones; native acceptance; fuel quanta and `AQUARIUS_JIT=0`; table equality).

### 3. Literals and `String.ParseNumber`

- `1.5` and `1e-9` are doubles, `2.5e3f` / `2f` floats, `2d` a double, `0xFF` / `0b1010` integers as 32-bit patterns
  (`0xFFFFFFFF` is -1), `_` between digits. Malformed numbers (`0xFG`, `1__0`, `1.5x`) are lexical errors; out-of-range
  literals are compile errors. Literal parsing now uses the invariant culture (the old `float.TryParse` used the
  current culture). Existing suffixed forms are unchanged. A scan of all 165 `.aqua` files in the repository found no
  program whose lexing changes.
- `字串.解析數字` / `String.ParseNumber` (builtin 57, a second String table because ids 46..48 are the conversions):
  integer text within 32 bits → integer, any other decimal/exponent/Infinity/NaN → the exactly rounded double (bignum
  correction, any number of digits, ties to even), else null.
- The bignum code of the shortest-digit printer moved unchanged from texts.h to the new shared `runtime/guest/decimal.h`.
- VS Code grammar (numbers, the Math module), LANGUAGE.md, docs, lexer/parser/runtime tests.
- There is no separate "agent reference" in the repository (only `docs/AquariusLang_Programming_Reference_v0.1.2.docx`,
  which I did not edit); LANGUAGE.md and docs/math.md carry the reference text.

### 4. Float `%`

- `binary()` in runtime.c, the generic emitter (`FloatRemainder`) and the native tier (`FloatOperation`) all call the
  guest's `rt_remainder` (musl `fmod`). After: `1e17 % 3` = 1, `1e300 % 7` = 1, `-0.0 % 3` = -0, `5 % Infinity` = 5,
  `x % 0` = NaN; `-7.5 % 2`, `7.5 % -2`, `1.5f % 0.4f` unchanged. Integer `%` unchanged.
- `FloatRemainderTest` (all tiers, compound assignments into integer variables, error case) and a shared vector.

### 5. Number text

- `decimal.h` `format_number_text` (exact Burger–Dybvig shortest digits, ties to even; fixed notation 1e-4..1e17,
  floats up to 1e9; otherwise `1E+17` with at least two exponent digits; `-0`, `NaN`, `Infinity`) is the one
  formatter: the guest's `String.From` and the host's `印出` (`runtime/host/format.c`, which now includes the header
  like it includes `layout.def`). The host no longer loops `snprintf`/`strtod`.
- 印出 output is unchanged: 8,154 random doubles and floats print byte-identically with the old and new hosts (the
  old windows-x64 pack of the main checkout was used read-only for the comparison). This required ties-to-even for
  floats too (the guest used to round float ties up).
- `String.From` changes: `1E+17` (was `100000000000000000`), `1E-05` (was `0.00001`), `1E+21` (was `1e+21`), `-0` (was
  `0`), `1E+09` for `1000000000.0f`, and float ties to even. Changed expected text: `tests/string-library.json` ("From
  uses shortest round-trip digits": `[..., 1E+20, 1E+21, 1E-06, 1E-07]`). No library `.expected` file changed.
- `NumberFormattingTest` compares 印出 and String.From on 650 random values and checks they read back exactly.

### 6. Bitwise operators

- `&`, `|`, `^`, `~`, `<<`, `>>` (arithmetic), `>>>` (logical) and `&= |= ^= <<= >>= >>>=` on variables, elements and
  fields. Integers only: a known non-integer is a compile error (TypeChecker), any other a runtime error ("Bitwise
  operators need integers / 位元運算需要整數: DOUBLE & INTEGER"). Shift counts use the low 5 bits. Precedence like
  C#/JS (shifts between sums and comparisons; `&`, `^`, `|` between equality and `&&`/`||`), so `a & b == c` is
  `a & (b == c)`.
- New IR ops BitAnd..ShiftRightUnsigned, BitNot (appended to `IrOperation`); runtime `binary()` ops 13..18 and
  `rt_bitNot`; inline i32 code in the generic emitter for proven integers (runtime call otherwise; integer shadows are
  not used for bitwise compound assignments); native tier support including compound forms. `Operators.cs` (new)
  holds the shared assignment/bitwise spellings. Lexer TODO branches removed.
- Tests: semantics, errors, parser precedence, lexer tokens, differential across tiers, native acceptance; the
  Starship example now covers every token (its test requires that).

### 8. Processing, GLM, Models, GL test helpers

- The graphics library vendors the same pinned musl subset (`libraries/graphics/third_party/musl`, rule 4: inside the
  library folder), renamed `aqua_graphics_*` by `aqua_libm.h` (the library links statically into the runtime beside
  the C library's libm) and compiled without FMA contraction (`-ffp-contract=off`, `-U__FP_FAST_FMA`). Processing's
  sin/cos/tan/asin/acos/atan/atan2/exp/log/pow and GLM Sin/Cos now equal `Math` bit for bit on every target (new
  library test `processing_math_parity`, passes on windows-x64 and web). Every existing expected output is unchanged.
- Left as Processing conventions, on purpose (would change Processing semantics, not test output): `round` halves up
  (`floor(x+0.5)`), domain errors instead of NaN/-Infinity, `min`/`max` of any count of finite numbers, its own RNG.
- `Models.aqua`'s 64-iteration Newton `_sqrt` → `數學.平方根`; the OpenGL test's Taylor `Sin`, `Cos` and Newton
  `Sqrt` (`libraries/opengl/tests/support/glm.aqua`) → `Math.Sin`/`Math.Cos`/`Math.Sqrt`.

### 9. `docs/int64-design.md`

Design only: language surface (`長整數` / `long`, `L` literals, promotion int < long < double), the tag decision (a new
tag 17 with a bit-test `numeric()` and a widening table instead of renumbering, annotation nibble 6), packed arrays,
SDK API 3 with an `aqua_value` union, hosts and web glue, compiler tiers, and a five-phase plan.

### Example

`examples/math_showcase/main.aqua` with `expected-output.txt`, run by `ExamplesTest`. Its output is identical on
windows-x64, on the web (Chromium via `scripts/web-run.mjs`) and on Linux (WSL, using the main checkout's existing
linux-x64 pack read-only — the guest runtime travels inside the `.wasm`, and that host's printing is byte-identical).

## Tests

Final run on the last commit (windows-x64 host):

- `dotnet test compiler/AquariusTests`: 897 passed, 0 failed, 4 skipped (Android/Linux-only cases). The full suite
  also passed after steps 1, 3, 4, 5 and 6 (856 → 897 tests); one failure on the way (the Starship token-coverage test
  after step 6) was fixed in that step.
- `scripts/test-libraries.ps1 -Targets windows-x64,web`: 83 passed, 0 failed, 51 skipped (cases that need a window,
  device or service). With `-Devices` on windows-x64: graphics `shapes_3d` and opengl `cube` (the ones affected by the
  Models / GL-helper changes) pass.
- Cross-target: `examples/math_showcase` prints identical output on windows-x64, web and Linux (WSL).
- Rough timing (Debug compiler, windows-x64): a typed loop of 3M iterations calling Math.Sqrt + Math.Sin +
  Math.Floor runs natively in 29 ms; 300k `Math.Sqrt` calls through a generic function value take 4 ms vs 150 ms for
  the former hand-written 64-step Newton `_sqrt` (same results).

## Conflicts with `perf/csharp-parity`

A trial `git merge-tree` of this branch with the committed `perf/csharp-parity` (f8f1fa8) auto-merges every file
except `compiler/AquariusTests/architecture/codegen-goldens.txt` (regenerate it; never hand-merge). Files both
branches change (auto-merged, but review): `LANGUAGE.md`, `TypeChecker.cs`, `parser/Parser.cs`, `WasmNativeFunction.cs`,
`LanguageFacts.cs`, `LanguageServerTest.cs`, `aquarius.tmLanguage.json`. The perf session also has uncommitted changes
to `runtime/guest/runtime.c`, `runtime/guest/runtime.wasm`, `ArrayLibraryTest.cs` and the goldens, which overlap this
branch's edits of runtime.c (`binary()`, `rt_call`, portable module table, `BINARY` list, exports), `runtime.wasm`
(binary: rebuild with `scripts/build-wasm-runtime.ps1` after merging, never pick a side) and the `PortableModules`
list. After merging: rebuild runtime.wasm, rebuild packs (host `format.c` and graphics changed), regenerate goldens,
run the suite. Semantic points to check: the perf branch's `WasmLoopVersioning.cs` and constant folding meet the new
IR operations (BitAnd..BitNot) and Math calls — unknown operations should simply not version/fold, but worth a test.

## Not done / notes

- Android: not run (no emulator session); the graphics musl copy is built with the same flags for every target, and
  the guest runtime is target-independent. Linux was checked only with the math showcase (old linux pack), not with
  rebuilt Linux packs or the library suite.
- The typed scalar tier (`WasmIntegerFunction`) does not take Math calls or bitwise operators (it accepts no builtins;
  extending it is optional since the native tier covers typed and cloned functions).
- Application size +39 KB (see step 1).
