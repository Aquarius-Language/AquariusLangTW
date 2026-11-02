# Numbers and the 數學 / Math module

AquariusLang has three number types: 32-bit integers (`整數` / `int`), 32-bit floats (`浮點數` / `float`) and 64-bit
doubles (`雙精度` / `double`). Every target runs the same guest runtime (`runtime/guest`), so arithmetic, the Math
module and number formatting give identical results on Windows, Linux, Android and the web.

## The 數學 / Math module

`數學` / `Math` is a portable module like `陣列` / `Array`: a global in both languages, also reachable through
`匯入("數學")` / `匯入("Math")`. Its names, arities and implementation live only in `runtime/guest/maths.h`.

| English | 中文 | Result |
| --- | --- | --- |
| `Abs(x)` | `絕對值` | Absolute value, same type (the minimum integer wraps to itself, like negation). |
| `Sign(x)` | `正負號` | -1, 0 or 1 in x's type; ±0 and NaN are returned unchanged. |
| `Min(a, b)`, `Max(a, b)` | `最小值`, `最大值` | The smaller / larger value in the wider type; NaN if either is NaN; -0 is below +0. |
| `Clamp(x, low, high)` | `限制範圍` | `Min(Max(x, low), high)` in the widest type. |
| `Floor`, `Ceil`, `Round`, `Trunc` | `向下取整`, `向上取整`, `四捨五入`, `截斷` | A whole number of the same type (an integer is unchanged). |
| `FloorInt`, `CeilInt`, `RoundInt`, `TruncInt` | `向下取整數`, `向上取整數`, `四捨五入取整數`, `截斷取整數` | The same as an integer, converted like `整數(x)`: saturating, NaN gives 0. |
| `Sqrt`, `Cbrt`, `Exp`, `Log`, `Log2`, `Log10` | `平方根`, `立方根`, `指數`, `對數`, `以二為底對數`, `以十為底對數` | |
| `Sin`, `Cos`, `Tan`, `Asin`, `Acos`, `Atan` | `正弦`, `餘弦`, `正切`, `反正弦`, `反餘弦`, `反正切` | Radians. |
| `Atan2(y, x)`, `Hypot(x, y)`, `Pow(x, y)` | `雙參數反正切`, `斜邊長`, `次方` | |
| `Sinh`, `Cosh`, `Tanh` | `雙曲正弦`, `雙曲餘弦`, `雙曲正切` | |
| `IsNaN(x)`, `IsFinite(x)` | `是非數`, `是有限數` | Booleans. |
| `Fmod(x, y)` | `浮點餘數` | The exact remainder of x / y truncated toward zero (C `fmod`). |
| `Random()` | `亂數` | A double in [0, 1). |
| `RandomInt(low, high)` | `亂數整數` | An integer in [low, high) (low when they are equal; high < low is an error). |
| `Seed(n)` | `亂數種子` | Restarts the generator from the integer n. |

Constants (doubles): `PI` / `圓周率`, `E` / `自然常數`, `TAU` / `二倍圓周率`, `Infinity` / `無限大`, `NaN` / `非數`.

Rules:

- **Types.** Abs, Sign, Min, Max, Clamp, Floor, Ceil, Round and Trunc keep the argument's type (the widest one for
  several arguments): an integer stays an integer, a float a float. The other functions return a double, or a float
  when the (widest) argument is a float, computed in double and rounded once to float.
- **IEEE 754, no domain errors.** `Log(0)` is `-Infinity`, `Sqrt(-1)` and `Log(-1)` are NaN, `Exp(1000)` is
  `Infinity`. Only a non-number argument (or a wrong argument count) is an error.
- **Rounding.** `Round` / `四捨五入` rounds halves **away from zero** (2.5 → 3, -2.5 → -3, -0.4 → -0), the rule people
  learn in school and C's `round`. It is not banker's rounding (.NET `Math.Round`'s default) nor JavaScript's
  `Math.round` (halves up: -2.5 → -2). `Round(0.49999999999999994)` is 0 (a naive `floor(x + 0.5)` gives 1).
- **Bit-identical everywhere.** `Sqrt`, `Floor`, `Ceil`, `Trunc`, `Min`, `Max` and `Abs` are the Wasm instructions
  (correctly rounded). The transcendental functions are musl 1.2.5's libm (`runtime/guest/third_party/musl`, vendored
  and pinned), compiled into the guest runtime, so no target's C library is involved. Arguments as large as 1e22 are
  reduced exactly (`Sin(1e22)` = -0.8522008497671888).
- **Random numbers.** PCG32 (O'Neill's `pcg32_random_r`, stream 0xda3e39cb94b95bdb). `Seed(n)` gives the same
  sequence on every target (`tests/math-library.json` pins one). Until `Seed` is called, the generator is seeded once
  from the host clock (`目前時間`), so unseeded programs differ between runs; a Thread worker starts from its parent's
  state at `Thread.Start`. `RandomInt` is unbiased (rejection sampling). The Processing library keeps its own
  `random` / `亂數` generator (a clone of .NET's `System.Random`) for compatibility.

The module costs about 39 KB in every application (the musl code and its tables are part of the guest runtime).

## Math in compiled code

Generic code calls the module through the runtime (`rt_call` → `math_call` in `maths.h`). The native tier
([execution tiers](execution-tiers.md#native-tier-for-fully-typed-functions)) compiles calls with number arguments
directly (`WasmMath`, `WasmNativeFunction.MathCall`): integer `Abs`, `Sign`, `Min`, `Max` and `Clamp` are i32
operations, `Sqrt`, `Floor`, `Ceil`, `Trunc`, `Abs`, `Min` and `Max` the f64 instructions (`f64.sqrt`, `f64.floor`,
`f64.min`, ...) the guest itself uses, and the other functions call the guest's exported `rt_math`, so a native call
returns exactly what the generic call returns (NativeMathTest compares both, including NaN, ±0 and ±Infinity). `Random`,
`RandomInt` and `Seed` stay generic. The entry adapter checks that no binding shadows `數學` / `Math`, like the core
conversions. The closed scalar tier (`WasmIntegerFunction`) accepts no builtin calls at all, so it does not take Math
calls either: functions calling Math are compiled by the native tier when typed, as clones when untyped.
