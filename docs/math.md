# Numbers and the 數學 / Math module

AquariusLang has three number types: 32-bit integers (`整數` / `int`), 32-bit floats (`浮點數` / `float`) and 64-bit
doubles (`雙精度` / `double`). Every target runs the same guest runtime (`runtime/guest`), so arithmetic, the Math
module and number formatting give identical results on Windows, Linux, Android and the web.

## Number literals

| Literal | Type | Notes |
| --- | --- | --- |
| `42`, `1_000_000` | integer | Decimal, at most 2147483647 (`-2147483648` is written with a minus sign). `_` may stand between two digits. |
| `0xFF`, `0b1010`, `0xFF_FF` | integer | Hexadecimal and binary, up to 32 bits, read as the two's complement pattern: `0xFFFFFFFF` is -1, `0x80000000` the minimum. |
| `1.5`, `1e-9`, `1E+3`, `2d`, `1.5d` | double | A fraction or exponent without a suffix makes a double; `d` makes digits a double. |
| `1.5f`, `2.5e3f`, `2f` | float | Rounded once from the decimal digits to 32 bits. |

A digit is needed on both sides of `.` (`.5` is not a number; `1.f` and `1.d` remain valid). A literal outside its
type's range (`2147483648`, `0x1_0000_0000`, `1e400`, `3.5e38f`) and a number running into letters or digits (`0xFG`,
`0b102`, `1__0`, `1_`, `1.5x`) are compile errors. Before these forms existed only suffixed decimals (`1.5f`, `1.5d`) existed: `1.5`
was a lexical error, and `0xFF`, `1e3`, `1_000` and `2d` split into a number and a name (a parse error).

`字串.解析數字(text)` / `String.ParseNumber(text)` reads decimal text at run time: surrounding whitespace, an optional
sign, digits with an optional fraction and exponent (`" 42 "`, `-1.5`, `.5`, `1E+17`), `Infinity` and `NaN`. Integer
text within 32 bits gives an integer, any other number a double (`"2147483648"`, `"1.0"`, `"1e3"`), and anything
else null (`"0x10"`, `"1e"`, `""`). The conversion is exact (the double nearest to the text, ties to even, any
number of digits), so `String.ParseNumber(String.From(x)) == x` for every double printed by 印出 or `String.From`.

## 64-bit integers (長整數 / long)

`整數` / `int` stays the 32-bit default. `長整數` / `long` is the opt-in 64-bit integer:

```aquarius
變數 毫秒: 長整數 = 1730505600000L;      # literals end in L
變數 雜湊 = 0xCBF29CE484222325L;           # hexadecimal and binary up to 64 bits (0xFFFF_FFFF_FFFF_FFFFL is -1)
雜湊 = (雜湊 ^ 97) * 0x100000001B3L;       # an int with a long gives a long; * wraps around like i64
印出(毫秒 / 1000, 型別(毫秒), 長整數(3.9), 整數(5000000000L), 字串.解析長整數("9223372036854775807"));
```

| Rule | Long |
| --- | --- |
| Literals | Integer digits (decimal, `0x`, `0b`, `_`) with the suffix `L`; `-9223372036854775808L` is the minimum. Without `L` an integer is 32-bit; an out-of-range one is a compile error that suggests the `L` form. |
| Mixing | int with long → long; long with float or double → double (C# would give float; a float cannot hold 25 bits). |
| Arithmetic | `+ - *` and negation wrap (two's complement); `/` and `%` truncate, a zero divisor is an error, the minimum `/ -1` wraps to itself. |
| Bitwise | `& \| ^ ~ << >> >>>`; shift counts use their low 6 bits (`1L << 64` is 1). |
| Comparisons | Exact: `9007199254740993L == 9007199254740992.0d` is false, `5L == 5` is true, NaN compares false. |
| Conversions | `長整數(x)` / `long(x)` truncates and saturates (NaN 0); `整數(long)` saturates; `雙精度(long)` and `浮點數(long)` round to nearest. |
| Typed bindings | `變數 t: 長整數`, parameters, results, struct fields and `長整數[]` arrays; ints widen to long, longs widen to float and double. A compound assignment keeps the variable's type, so `n += 5000000000L` saturates an `整數` n. |
| Text | Plain decimal digits in `印出` and `String.From`; `字串.解析長整數` / `String.ParseLong` reads optionally signed digits within 64 bits, else null. |
| Hash keys | Typed: `{5: "int", 5L: "long"}` has two keys. |
| Math | `Abs`, `Sign`, `Min`, `Max`, `Clamp` keep longs; `Floor`..`Trunc` return them unchanged; `*Int` saturate to int; other functions compute on doubles. `Math.Seed` takes a long. |
| Threads | Longs travel to and from workers exactly. |

A long is the value tag 17; its two's complement bits fill the 8 bytes a number uses (`Value.number`), so nothing
else in the value layout changes. Generic compiled code keeps proven longs in their f64 registers and computes `+ - *`,
comparisons and bitwise operators inline as `i64` instructions on the reinterpreted bits; division, remainder,
operations with floats or doubles and unknown types call the runtime. The native tier does not compile functions that
use longs yet (they run as generic code), and `長整數[]` arrays store whole values (not packed) — see
[the design](int64-design.md) for the remaining phases. Native libraries see longs through SDK API 3: `AQUA_LONG`,
`aqua_value.integer`, `aqua_long()`, `aqua_arg_long()`, `aqua_return_long()`, `aqua_is_numeric()` and `aqua_number_of()`;
`aqua_arg_number` accepts a long (rounded to a double) and `aqua_is_number` stays false for one, so existing libraries
never misread its bits.

## Printing numbers

`印出` / `print` (the host, `runtime/host/format.c`) and `字串.轉文字` / `String.From` (the guest, `texts.h`) format numbers
with the same code, `runtime/guest/decimal.h`, so a number has one text on every target and in both places:

- the shortest digits that read back to the same double (or float, for floats), the closest such digits, exact ties
  to the even digit (an exact bignum algorithm, Burger & Dybvig);
- fixed notation from 10^-4 up to 10^17 for doubles and 10^9 for floats, otherwise `d.dddE±XX` with at least two
  exponent digits: `1E+17`, `1E-05`, `1.5E+300`, `1E+09` (a float);
- `-0`, `NaN`, `Infinity`, `-Infinity`; integers as plain decimals.

This is the style 印出 always had. Before, `String.From` followed JavaScript (`1e+21`, `1e-7`, -0 shown as `0`, plain
digits up to 10^21), and the host found its digits by trying `printf` precisions until `strtod` read the value back,
which depended on each C library's rounding. On 8,154 random doubles and floats 印出 prints exactly what it printed
before; `String.From` changed only in the style above (and for floats on an exact tie, where it rounded up).

| Value | 印出 before and now | String.From before | String.From now |
| --- | --- | --- | --- |
| `100000000000000000.0d` | `1E+17` | `100000000000000000` | `1E+17` |
| `0.00001d` | `1E-05` | `0.00001` | `1E-05` |
| `1e21` | `1E+21` | `1e+21` | `1E+21` |
| `-(0.0d)` | `-0` | `0` | `-0` |
| `1000000000.0f` | `1E+09` | `1000000000` | `1E+09` |
| `3047523.25f` | `3047523.2` | `3047523.3` | `3047523.2` |

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

## The % operator

Integer `%` truncates toward zero like integer division and fails on a zero divisor. When either operand is a float
or double, `%` is the exact IEEE remainder of truncated division — C's `fmod`, implemented once in the guest
(`rt_remainder`, musl's `fmod`) and called by the generic runtime, generic compiled code and native code alike. Its
sign is the dividend's. Before, every tier computed `x - trunc(x / y) * y`, which is inexact for large quotients:

| Expression | Before | Now |
| --- | --- | --- |
| `100000000000000000.0d % 3` | 0 | 1 |
| `1e300 % 7` | 0 | 1 |
| `-0.0 % 3` | 0 | -0 |
| `5.0 % Infinity` | NaN | 5 |
| `-7.5 % 2`, `7.5 % -2`, `1.5f % 0.4f` | -1.5, 1.5, 0.29999998 | unchanged |

`數學.浮點餘數` / `Math.Fmod` is the same function. A compound `%=` into an integer variable computes in double and
converts back like the other compound operators (`變數 n = 1000000000; n %= 0.7d;` gives 0).

## Integer bitwise operators

| Operator | Meaning | Example |
| --- | --- | --- |
| `a & b`, `a \| b`, `a ^ b` | and, or, exclusive or | `6 & 3` is 2, `6 \| 3` is 7, `6 ^ 3` is 5 |
| `~a` | complement | `~5` is -6 |
| `a << n` | left shift (wraps around) | `1 << 31` is -2147483648 |
| `a >> n` | arithmetic right shift (keeps the sign) | `-16 >> 2` is -4 |
| `a >>> n` | logical right shift (zeros enter) | `-1 >>> 1` is 2147483647 |
| `&= \|= ^= <<= >>= >>>=` | compound assignment | `旗標 \|= 1 << 3;` |

Operands are 32-bit integers only. A float, double, Boolean or string operand is a compile error when its type is known
(`1.5 & 1`, `變數 x: 雙精度 = 1; x &= 2;`) and a runtime error otherwise ("Bitwise operators need integers / 位元運算
需要整數: DOUBLE & INTEGER"). Shift counts use their low 5 bits, exactly like Wasm's `i32.shl` / `i32.shr_s` /
`i32.shr_u`: `1 << 32` is 1, `1 << 33` is 2, `1 << -1` is `1 << 31`. Hexadecimal and binary literals
(`0xFF00`, `0b1010`) make masks readable.

Precedence follows C# and JavaScript (from loosest): assignments, `&&` / `||`, `|`, `^`, `&`, `==` / `!=`,
comparisons, shifts, `+` / `-`, `*` / `/` / `%`, prefix `-` `!` `~`. So `1 + 2 << 1` is 6, `a & b == c` is
`a & (b == c)` (an error for integers: write `(a & b) == c`), and `a | b && c | d` is `(a | b) && (c | d)`.

The generic runtime (`binary` in `runtime.c`, `rt_bitNot`), generic compiled code (inline i32 instructions when both
operands are proven integers, the runtime otherwise) and native code (typed and cloned functions, loops) give identical
results; `BitwiseOperatorTest` compares them. Compound bitwise assignments of module globals go through the runtime
rather than the integer shadows the generic emitter keeps for `+=`.

## Processing, GLM and library code

The graphics library's Processing (`繪圖`) and `GLM` modules are native C. Their transcendental functions (`sin`, `cos`,
`tan`, `asin`, `acos`, `atan`, `atan2`, `exp`, `log`, `pow`; GLM `Sin`, `Cos`) now run the same musl code as `數學` /
`Math`, vendored into the library (`libraries/graphics/third_party/musl`, renamed `aqua_graphics_*`, compiled without
fused multiply-adds), so `p.sin(x) == Math.Sin(x)` for every `x` on every target (`tests/processing_math_parity.aqua`);
before, each target's C library computed them and could differ in the last bit. `sqrt` was already exact everywhere.
Every existing expected output stays the same.

Processing keeps its documented conventions, which differ from `Math` on purpose: `round` rounds halves up
(`floor(x + 0.5)`: `round(-2.5)` is -2, `Math.Round(-2.5)` is -3), a result outside a function's domain (`sqrt(-1)`,
`log(0)`) is an error rather than NaN or -Infinity, `min` / `max` take any number of finite numbers or an array, and
`random` / `randomSeed` keep their own generator (a clone of .NET's `System.Random`).

The `Models` module (`libraries/graphics/aqua/Models.aqua`) and the OpenGL cube test's GLM helpers
(`libraries/opengl/tests/support/glm.aqua`) used hand-written Newton square roots and a Taylor-series sine; they now call
`數學.平方根` / `Math.Sqrt`, `Math.Sin` and `Math.Cos`.
