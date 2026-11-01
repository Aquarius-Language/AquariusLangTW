# AquariusLang (星泉) reference for AI agents

This is a self-contained reference for writing, building and running AquariusLang programs with only the packaged
`aqua` distribution (no source repository). It was written from the compiler, runtime and library sources of
AquariusLang 0.1.6 and every example below with an `output` block was run against the packaged build. Where this file
and the behavior of your `aqua` disagree, trust `aqua` and the files it ships with (see
[Finding the exact API](#21-finding-the-exact-api)).

Contents: [1 Quick facts](#1-quick-facts-read-first) · [2 Installation](#2-installation-and-the-package) ·
[3 CLI](#3-the-aqua-command-line) · [4 Program structure](#4-program-structure-and-lexical-rules) ·
[5 Values and types](#5-values-and-types) · [6 Operators](#6-operators) · [7 Variables and control flow](#7-variables-scope-and-control-flow) ·
[8 Functions](#8-functions-and-closures) · [9 Optional type annotations](#9-optional-type-annotations) ·
[10 Arrays](#10-arrays-and-the-array-module) · [11 Hashes](#11-hashes-and-the-hash-module) ·
[12 Strings](#12-strings-and-the-string-module) · [13 Structs](#13-structs) · [14 Modules](#14-modules-and-imports) ·
[15 Async](#15-async-functions-and-promises) · [16 Threads](#16-threads) · [17 Core globals](#17-core-global-functions) ·
[18 Errors](#18-errors-and-error-messages) · [19 Bundled libraries](#19-bundled-libraries) ·
[20 Bottle projects and external libraries](#20-bottle-projects-and-external-libraries) ·
[21 Finding the exact API](#21-finding-the-exact-api) · [22 Build targets](#22-build-targets-and-deployment) ·
[23 Pitfalls checklist](#23-pitfalls-checklist)

## 1. Quick facts (read first)

AquariusLang is a small, dynamically typed, bilingual (Traditional Chinese and English) language compiled to
WebAssembly. Every keyword and builtin has an English and a Chinese spelling; they are interchangeable and can be mixed
in one file. Write English unless asked otherwise. It looks like JavaScript but differs in ways that break code
written by habit:

- **Functions are values**: `var add = function(a, b) { return a + b; };`. There is no `function name() {}` statement,
  no `let`/`const`, no `fn`, no arrow functions.
- **Decimal literals need a suffix**: `0.5d` (double) or `0.5f` (float). A bare `0.5` is a syntax error. No exponent
  literals (`1e3`), no hex literals.
- **Only `for` loops**: no `while` or `do`. Write `for (; condition;) { ... }` or `for (;;) { ... break; }`.
- **No `?:`, no `--`, no `try`/`catch`/`throw`, no `switch`, no classes, no spread, no destructuring, no default
  parameter values, no slices (`a[1:2]`), no `var a = 1, b = 2;`.** Use `x -= 1` instead of `x--`.
- **`&&` and `||` have the same precedence and group left to right**: `a || b && c` is `(a || b) && c`. Parenthesize
  mixed chains. Both short-circuit, and an operand that runs must be a Boolean (`1 && true` is an error).
- **Truthiness**: only `false` and `null` are false in `if`/`for` conditions. `0`, `""` and `[]` are true.
- **No implicit conversions in `+`**: `"a" + 1` is an error. Use `String.From(1)`. `<`/`>` compare numbers only, not strings.
- **Integers are 32-bit** and wrap on overflow; `7 / 2` is `3`. Use `7.0d / 2` or `double(7) / 2` for 3.5.
- **Strings**: double quotes only. The only escapes are `\"` and `\\`; `"\n"` is a backslash and an `n`. Get a newline
  from a multi-line literal or `String.FromCode(10)`.
- **Hashes have no dot access**: `h["key"]`, never `h.key`. Structs and library objects use dots.
- **Missing hash keys and out-of-range array reads give `null`** (typed arrays raise an error instead).
- **Calls check arity exactly**: calling a 2-parameter function with 1 argument is a runtime error.
- **`print` shows Booleans as `真` / `假`** (true / false) and strings without quotes.
- **Every `import("file.aqua")` makes a new module instance** (not a cached singleton).
- **`aqua build a.aqua` compiles only the files you list**; list every imported script (or use `aqua run main.aqua`,
  or a project `aqua.toml`).
- **Comments**: `#` to end of line. `##` starts a *block* comment that ends at the next `##`. There is no `//`.
- **Runtime errors stop the program**, print `ERROR: <message>` on standard output and exit with code 1. There is no
  way to catch them except through promise rejection (`Catch`) and library helpers such as `Network.Try`.

```aqua
# A complete program: types, a closure, a hash, a loop and a struct.
var greet = function(name) { return "Hello, " + name + "!"; };
print(greet("Aquarius"));

var scores = {"ann": 90, "bob": 85};
scores["cy"] = 77;
var names = Hash.Keys(scores);
var total = 0;
for (var i = 0; i < len(names); i++) {
    total += scores[names[i]];
}
print("average", total / len(names), double(total) / len(names));

struct Point { x: double; y: double }
var p = Point(3.0d, 4.0d);
p.x += 1.5d;
print(p, p.x * p.y);
print(10 > 3 && (false || true));
```

```output
Hello, Aquarius!
average 84 84
Point{x: 4.5, y: 4} 18
真
```

## 2. Installation and the package

The package is one folder (for example `C:\aqua` or `~/aqua`); keep it together. Put that folder on `PATH` so `aqua`
(`aqua.exe` on Windows) runs from anywhere. It needs no .NET, no compiler and no other installation.

```text
aqua/
  aqua(.exe)                  the CLI: compiler, packager, REPL, Bottle package manager
  AGENT_REFERENCE.md          this file
  runtime/<target>/           runtime packs: windows-x64 (aquarius.exe), linux-x64, web, android-arm64-v8a, android-x86_64
  libraries/<name>/           the bundled libraries: aqua.toml, README.md, docs/, aqua/*.aqua (Aquarius-source modules)
  web-glue/                   the JavaScript of exported web sites
  sdk/                        headers and CMake files for writing native (C/C++/Rust) libraries
  examples/                   example programs (each folder has a README)
  scripts/                    toolchain pins and installers (only for building native libraries)
```

Set `PATH` for the current PowerShell session, or permanently for the user:

```powershell
$env:Path = "C:\aqua;" + $env:Path
[Environment]::SetEnvironmentVariable("Path", "C:\aqua;" + [Environment]::GetEnvironmentVariable("Path", "User"), "User")
aqua --version          # AquariusLang 0.1.6
```

On Linux: `export PATH="$HOME/aqua:$PATH"`. Programs print UTF-8. In Windows PowerShell 5.1, set
`[Console]::OutputEncoding = [Text.Encoding]::UTF8` before capturing output that contains Chinese (such as `真`/`假`).

Source files are UTF-8 (a BOM is accepted; UTF-16 is not). When writing `.aqua` files from PowerShell 5.1, use
`[IO.File]::WriteAllText(path, text, (New-Object Text.UTF8Encoding $false))` or `Set-Content -Encoding utf8`, not
`Out-File` defaults.

## 3. The aqua command line

```text
aqua run (main.aqua | app.wasm | project-directory) [--entry main.aqua] [argument ...]
aqua build main.aqua [more.aqua ...] [--root dir] [--assets path ...] [--entry main.aqua] [-o app.wasm]
aqua build (main.aqua ... | app.wasm) --target web -o site-directory
aqua build (main.aqua ... | app.wasm) --target windows -o app.exe [--hide-terminal true|false] [--onnx-cuda true|false]
aqua build (main.aqua ... | app.wasm) --target linux -o app [--onnx-cuda true|false]
aqua build (main.aqua ... | app.wasm) --target android -o app.apk [--application-id org.example.app] [--app-name Name] [--keystore file --key-alias name]
aqua build [aqua.toml | project-directory] [--target PLATFORM] [--platform PLATFORM] [-o output] [--locked] [--offline]
aqua new NAME [--type executable|library] [-o directory]
aqua repl
aqua --version | --help
aqua bottle init|install|uninstall|restore|update|list|cache ...      (section 20)
aqua library build [folder] [--targets windows-x64,linux-x64,android-arm64-v8a,android-x86_64,web]
```

- **`aqua run main.aqua`** compiles `main.aqua` plus every script its string literals name (for example
  `import("lib/tools.aqua")`), then runs it. This is the fastest edit-run loop. Arguments after the file are passed to
  the program as launch arguments (see `Application.LaunchFiles()` in section 19).
- **`aqua build`** compiles exactly the sources you list into one standard `.wasm` (default `-o` is next to the
  entry). `--root` is the source root that keeps relative module and asset paths (default: the current folder);
  `--assets` adds a file or folder (repeatable; must be under the root); `--entry` picks the first script (default: the
  first source). Building never runs the program, and a failed build leaves the old output in place.
- **`aqua run app.wasm`** runs a compiled application on this machine's runtime pack. A project folder or `aqua.toml`
  builds and runs the project, including its Bottle libraries.
- **`aqua repl`** is interactive (`>>` prompt; Ctrl+Z/Ctrl+D ends). Each input reruns the earlier accepted inputs.
  It is meant for people; agents should write files and use `aqua run`.
- **`aqua new MyApp`** creates `MyApp/aqua.toml` and `MyApp/main.aqua`; `aqua build MyApp` writes
  `MyApp/dist/MyApp.wasm`.
- **Exit codes**: 0 success; 1 compilation, runtime or I/O failure; 2 invalid command or options. `aqua run` returns the
  program's exit code (0, or 1 after a runtime error or an unhandled promise rejection).
- **Output streams**: the program's `print` and runtime errors (`ERROR: ...`) go to standard output; compile errors and
  CLI messages (`Compiled 2 script(s) to ...`) go to standard error.
- When a script finishes with a value that is not `null` (a final expression statement, or a top-level `return 5;`),
  `aqua run` prints that value. End scripts with a statement such as `print(...)` to avoid surprises.

Environment variables:

| Variable | Effect |
| --- | --- |
| `AQUA_BOTTLE_HOME` | Bottle cache folder (default `~/.aquarius/bottles`) |
| `AQUARIUS_GRAPHICS_FRAMES=N` | Processing `run()` stops after N frames (headless tests) |
| `AQUARIUS_GRAPHICS_CAPTURE=file.png` | saves the last drawn frame |
| `AQUARIUS_GRAPHICS_RECORD=frames/####.png` | saves every frame; `millis()` advances one frame period per frame |
| `AQUARIUS_JIT=0` | turns off the desktop runtime JIT (same results, slower) |
| `SSL_CERT_FILE=ca.pem` | adds trusted root certificates for the native network client |
| `AQUARIUS_STORAGE_ROOT`, `AQUARIUS_APPLICATION_ID` | where `Storage` keeps settings and data |
| `AQUARIUS_KEYSTORE_PASSWORD`, `AQUARIUS_KEY_PASSWORD` | Android signing passwords |

## 4. Program structure and lexical rules

A program is a sequence of statements run top to bottom. Semicolons end statements; they may be left out at line
ends, but write them. Blocks use braces and are required for `if`/`for`/function bodies.

- **Comments**: `# line comment`; `## block comment ##` (everything between two `##`, across lines).
- **Identifiers**: Unicode letters or `_`, then letters, digits or combining marks (`名字`, `naïve`, `_x1`).
- **Keywords** (case-sensitive, reserved in both spellings):

| English | 繁體中文 | Meaning |
| --- | --- | --- |
| `var` | `變數` | declare a variable |
| `function` | `函式` | function literal |
| `return` | `回傳` | return a value (`return;` returns `null`) |
| `if` / `else if` or `elif` / `else` | `如果` / `否則如果` / `否則` | conditional (also an expression) |
| `for` | `迴圈` | the only loop |
| `break` / `continue` | `中斷` / `繼續` | loop control |
| `true` / `false` | `真` / `假` | Booleans |
| `null` | `空值` | the null value |
| `struct` | `結構` | struct declaration (keyword only where a declaration starts) |
| `async` | `非同步` (also `非同步函式`) | async function (keyword only before `function`/`函式`) |
| `await` | `等待` | await a promise (keyword only where it starts an await) |

  `let`, `fn`, `while`, `class`, `new`, `this` are **not** keywords (they are ordinary identifiers, and `while`
  loops do not exist). Longer names that contain a keyword (`returnValue`, `如果成立`) are fine. Using a keyword as a
  name (`var if = 1;`) is a syntax error.
- **Integer literals**: `0`, `42`, `-2147483648` (32-bit; larger literals are compile errors).
- **Float literals**: `1.5f`, `2f` is not valid (write `2.0f` or `float(2)`). **Double literals**: `0.25d`, `3.0d`.
- **Strings**: `"text"`, may span lines (the newline is kept). Escapes: only `\"` and `\\`. Any other backslash is
  literal (`"C:\temp\new"` is fine). No single quotes, no template strings.
- **Arrays** `[1, "two", 3.0d]`, **hashes** `{"key": 1, 2: "two", true: "t"}`.

## 5. Values and types

`typeOf(value)` / `型別(值)` returns a stable tag:

| Tag | Values |
| --- | --- |
| `integer` | 32-bit signed integers, wrap-around arithmetic |
| `float` | 32-bit floats (`1.5f`, `float(x)`) |
| `double` | 64-bit floats (`1.5d`, `double(x)`, `currentTime()`) |
| `string` | immutable UTF-16 text |
| `boolean` | `true` / `false` |
| `null` | `null` |
| `array` | ordered, growable, mixed values; reference semantics |
| `hash` | insertion-ordered map; keys are typed (`1`, `1.0d` and `"1"` are different keys) |
| `function` | script functions, async functions, builtins |
| `module` | an imported script or library module |
| `promise` | from async functions and `Promise` |
| a struct name | struct values (`typeOf(Point(1, 2))` is `"Point"`); the struct type itself is `"type"` |
| `record`, `resource`, `stream`, ... | objects returned by libraries (lower-cased class names, e.g. `audio.device`) |

Arrays, hashes, structs and library objects are references: assignment and calls share the same object, and `==`
compares identity (`[1] == [1]` is false). Numbers compare by value across types (`1 == 1.0d` is true); values of
different kinds are simply unequal (`1 == "1"` is false, `null == false` is false).

Printing (`print`, `String.From`): integers as digits, doubles and floats in shortest round-trip form (`3.0d` prints
`3`, `0.1d + 0.2d` prints `0.30000000000000004`, `1.0f / 3.0f` prints `0.33333334`, large values like `1E+23`), Booleans
as `真`/`假`, `null` as `null`, arrays as `[a, b]`, hashes as `{k: v}`, structs as `Name{field: value}`, strings
without quotes. Use `Serialization.Json(value)` (section 19) for an unambiguous form.

## 6. Operators

Precedence from lowest to highest:

| Level | Operators | Notes |
| --- | --- | --- |
| 1 | `+= -= *= /= %=` | on variables, `a[i]`, `h[k]`, `obj.field` |
| 2 | `=` | assignment |
| 3 | `&&` `\|\|` | **same level, left to right**, short-circuit, Boolean operands |
| 4 | `==` `!=` | |
| 5 | `<` `>` `<=` `>=` | numbers only |
| 6 | `+` `-` | `+` also joins two strings |
| 7 | `*` `/` `%` | |
| 8 | prefix `-` `!` `++` | `!` needs a Boolean |
| 9-12 | `.` member, `(...)` call, `[...]` index, postfix `++` | |

- Mixed numeric operands promote: double beats float beats integer (`1 + 0.5d` is a double).
- Integer `/` truncates toward zero (`-7 / 2` is `-3`); `%` takes the sign of the left operand (`-7 % 2` is `-1`).
  Integer division or remainder by zero is a runtime error; floating division by zero gives `Infinity`/`NaN`.
- Integer `+ - *` wrap: `2147483647 + 1` is `-2147483648`.
- `++x` returns the new value, `x++` the old one; both keep the variable's numeric type. There is **no `--`**.
- Compound assignment keeps the left side's type: with `var n: int = 5; n += 2.7d;` `n` becomes `7`.
- There is no `**` (use `Processing.pow` or a loop), no bitwise operators, no `?:`, no `===`.

```aqua
print(7 / 2, 7.0d / 2, 7 % 3, -7 / 2, -7 % 2, 2147483647 + 1);
print(1 + 0.5d, typeOf(1 + 0.5f), typeOf(1.5f + 0.5d));
var x = null;
print(x == null || x > 3);          # short-circuit: x > 3 is never evaluated
print(true || false && false);      # (true || false) && false
print(true || (false && false));
var i = 5;
print(i++, i, ++i);
```

```output
3 3.5 1 -3 -1 -2147483648
1.5 float double
真
假
真
5 6 7
```

## 7. Variables, scope and control flow

- `var name = value;` declares in the current block scope. One name per `var`. Assigning an undeclared name is an
  error; reading one gives `ERROR: Identifier not found: name`.
- `var add, 加法 = function(a, b) { return a + b; };` gives one function two names (only for function literals).
- Declarations shadow global builtins (`var len = 3;` hides `len`). Avoid shadowing `print`, `len`, `String`, `Array`,
  `Hash`, `Thread`, `Promise`.
- `if`/`elif`/`else` and `for` need parentheses and braces. `if` is also an expression whose value is the last
  expression of the branch taken: `var r = if (a > 2) { "big"; } else { "small"; };`.
- `for (init; condition; update)`: each part may be empty. The loop variable is local to the loop, and closures made
  in the body keep their own iteration's value (unlike JavaScript `var`).

```aqua
var v = 5;
if (v < 0) { print("negative"); } elif (v == 5) { print("five"); } else { print("other"); }
var size = if (v > 3) { "big"; } else { "small"; };
print(size);

for (var i = 0; i < 6; i++) {
    if (i == 1) { continue; }
    if (i == 4) { break; }
    print("i", i);
}
var n = 3;
for (; n > 0;) { n -= 1; }           # a while loop
var steps = 0;
for (;;) { steps++; if (steps == 3) { break; } }
print(n, steps);

var fs = [];
for (var k = 0; k < 3; k++) { Array.Append(fs, function() { return k; }); }
print(fs[0](), fs[1](), fs[2]());
```

```output
five
big
i 0
i 2
i 3
0 3
0 1 2
```

## 8. Functions and closures

- Function literal: `function(a, b) { ... }`. Store it in a variable to name it; recursion works through that name.
- `return value;` returns; `return;` returns `null`; without `return` the value of the last expression statement is
  the result.
- Arity is exact: too few or too many arguments is a runtime error (`Function expects 2 arguments, got 1.`). There are
  no default values and no rest parameters; pass an array or a hash for optional data.
- Functions are closures and capture variables by reference.
- `arityOf(f)` gives the declared parameter count (-1 for native functions).

```aqua
var fact = function(n) { if (n < 2) { return 1; } return n * fact(n - 1); };
print(fact(10));

var makeCounter = function() {
    var count = 0;
    return function() { count += 1; return count; };
};
var next = makeCounter();
next();
next();
print(next());

var twice = function(f, x) { return f(f(x)); };
print(twice(function(v) { v * 3; }, 2), arityOf(twice));

var options = function(config) {
    return Hash.Get(config, "size", 10);   # optional settings through a hash
};
print(options({}), options({"size": 4}));
```

```output
3628800
3
18 2
10 4
```

## 9. Optional type annotations

Annotations go after a name with `:`; they are checked (at compile time when provable, otherwise at run time) and let
the compiler emit faster native code. Untyped code is always fine.

| English | 繁體中文 | Type |
| --- | --- | --- |
| `int`, `float`, `double`, `bool`, `string` | `整數`, `浮點數`, `雙精度`, `布林`, `字串` | scalars |
| `int[]`, `float[]`, `double[]`, `bool[]`, `string[]` | `整數[]` ... | typed arrays: every element has that type; out-of-range access is an error |
| `array` | `陣列` | any array |
| a struct name | 結構名稱 | that struct, or `null` |

- `var count: int = 0;`, `var f = function(n: int, scale: double): double { ... };`
- Integers widen to float/double automatically; narrowing needs `int(x)` (truncates toward zero, saturates, NaN is 0),
  `float(x)` or `double(x)`. These three also convert between numeric types; they do not parse strings (use
  `String.ParseInt`).
- `null` fits only struct annotations. A visible mismatch is a compile error:
  `Type mismatch: n expects 整數 int, got 字串 string.`
- `var a: double[] = Array.Make(n, 0.0d);` (use `0.0d`, not `0`, to make a `double[]`).

```aqua
var fib = function(n: int): int {
    if (n < 2) { return n; }
    return fib(n - 1) + fib(n - 2);
};
var ratio = function(a: int, b: int): double { return double(a) / b; };
var flags: bool[] = Array.Make(4, false);
flags[2] = true;
print(fib(20), ratio(1, 4), flags, int(3.9d), int(-3.9d), float(1) / 3);
```

```output
6765 0.25 [假, 假, 真, 假] 3 -3 0.33333334
```

## 10. Arrays and the Array module

- Literal `[1, 2, 3]`, index `a[0]`, write `a[i] = v` (only inside `0..len-1`; writing past the end is an error, use
  `Array.Append`). Reads outside the range (including negative indexes) give `null`.
- Global helpers: `len(a)`, `last(a)` (null if empty), `rest(a)` (copy without the first element), `push(a, v)`
  (**returns a new array, `a` is unchanged**).
- `Array` / `陣列` is built in (no import needed; `import("Array")` also works). Mutating functions change the array in
  place and return it:

| English | 繁體中文 | Arguments | Result |
| --- | --- | --- | --- |
| `Make` | `建立` | length, value, capacity? | new array filled with value |
| `Len` / `Cap` | `長度` / `容量` | array | length / capacity |
| `Grow` | `擴充` | array, additional | reserve capacity |
| `Append` | `追加` | array, value | append in place |
| `AppendArray` | `追加陣列` | array, source | append all in place |
| `Copy` | `複製` | dest, src, destStart?, srcStart?, count? | copies, returns count |
| `Clone` | `克隆` | array | shallow copy |
| `Slice` | `切片` | array, start, end | copy of `[start, end)` |
| `Fill` | `填入` | array, value | fill in place |
| `Delete` | `刪除` | array, start, end | remove `[start, end)` in place |
| `Reverse` | `反轉` | array | in place |
| `Index` / `Contains` | `尋找` / `包含` | array, value | first index or -1 / Boolean |
| `Sort` | `排序` | array, compare? | in place, stable; numbers or strings, or `compare(x, y) < 0` means x first |
| `Sum` / `Min` / `Max` | `總和` / `最小值` / `最大值` | array | number (Min/Max: null for `[]`) |
| `Map` / `Filter` | `映射` / `篩選` | array, function | new array |
| `Reduce` | `歸約` | array, function(acc, x), initial | value |

`floodFill(grid, width, height, x, y, value)` / `區域填色` flood-fills a flat numeric grid and returns the changed count.

```aqua
var a = [3, 1, 2];
Array.Append(a, 10);
var b = push(a, 99);
print(a, len(a), len(b), last(a), rest(a), a[10]);
print(Array.Sort(a), Array.Sort(["pear", "apple"]), Array.Sort([3, 1, 2], function(x, y) { return y - x; }));
print(Array.Map([1, 2, 3], function(x) { return x * x; }),
      Array.Filter([1, 2, 3, 4], function(x) { return x % 2 == 0; }),
      Array.Reduce([1, 2, 3], function(acc, x) { return acc + x; }, 0));
print(Array.Slice([1, 2, 3, 4], 1, 3), Array.Index([5, 6], 6), Array.Sum([1, 2.5d]), Array.Max(["a", "z"]));
var grid = Array.Make(3, 0);
grid[1] += 5;
grid[2]++;
print(grid);
```

```output
[3, 1, 2, 10] 4 5 10 [1, 2, 10] null
[1, 2, 3, 10] [apple, pear] [3, 2, 1]
[1, 4, 9] [2, 4] 6
[2, 3] 1 3.5 z
[0, 5, 1]
```

## 11. Hashes and the Hash module

- Literal `{"name": "Ann", 1: "one", true: "yes"}`. Keys are strings, numbers or Booleans and are typed. Structs,
  arrays and hashes cannot be keys.
- `h[key]` reads (missing gives `null`), `h[key] = v` adds or replaces, `h[key] += 1` and `h[key]++` work.
  **No `h.key`** (`ERROR: Cannot access member of HASH`).
- `len(h)` counts entries. Iteration order is insertion order.
- `Hash` / `雜湊` (built in): `Keys`/`鍵列表`, `Values`/`值列表`, `Entries`/`項目列表` (`[key, value]` pairs), `Has`/`包含鍵`,
  `Get(h, key, default)`/`取得`, `Delete(h, key)`/`刪除鍵`, `Len`/`長度`, `Clone`/`克隆`, `Clear`/`清空`,
  `Merge(dest, src)`/`合併`.

```aqua
var ages = {"ann": 31, "bob": 27};
ages["cy"] = 40;
ages["bob"] += 1;
var keys = Hash.Keys(ages);
for (var i = 0; i < len(keys); i++) { print(keys[i], ages[keys[i]]); }
print(ages["nobody"], Hash.Has(ages, "ann"), Hash.Get(ages, "zed", 0), len(ages));
Hash.Delete(ages, "ann");
print(Hash.Entries(ages));
```

```output
ann 31
bob 28
cy 40
null 真 0 3
[[bob, 28], [cy, 40]]
```

## 12. Strings and the String module

- Strings are immutable UTF-16; `len(s)` counts code units; `s[i]` is a one-character string (`null` out of range).
  `s[i] = x` is an error.
- `+` joins two strings only. `s += t` in a loop is efficient (linear).
- `String` / `字串` (built in):

| English | 繁體中文 | Arguments | Result |
| --- | --- | --- | --- |
| `From` | `轉文字` | value | text form (as `print` shows it) |
| `Slice` | `切片` | text, start, end | `[start, end)`; indexes must be in range |
| `Find` / `Contains` | `尋找` / `包含` | text, part | index or -1 / Boolean |
| `StartsWith` / `EndsWith` | `開頭是` / `結尾是` | text, part | Boolean |
| `Split` | `分割` | text, separator | array (keeps empty fields; `""` splits characters) |
| `Join` | `合併` | array, separator | text |
| `Replace` | `取代` | text, old, new | replaces all |
| `Trim` | `修剪` | text | trims whitespace (also U+3000) |
| `Upper` / `Lower` | `轉大寫` / `轉小寫` | text | ASCII and full-width letters |
| `Repeat` | `重複` | text, count | text |
| `Code` | `字元碼` | text, index | code point or null |
| `FromCode` | `從字元碼` | codePoint | one-character text (`String.FromCode(10)` is a newline) |
| `ParseInt` | `解析整數` | text | 32-bit integer or null |

There is no string comparison operator and no float parser. To parse a decimal number use
`Serialization.ParseJson("3.25")` (section 19). To compare strings for sorting use `Array.Sort` on strings.

```aqua
var NL = String.FromCode(10);
var csv = "name,qty" + NL + "apple,3" + NL + "pear,5";
var lines = String.Split(csv, NL);
var total = 0;
for (var i = 1; i < len(lines); i++) {
    var cells = String.Split(lines[i], ",");
    total += String.ParseInt(cells[1]);
}
print(len(lines), total, String.Upper(lines[1]), String.Join(["a", "b", "c"], "-"));
print(String.Find("hello", "l"), String.Slice("hello", 1, 3), String.Replace("a.b.c", ".", "/"), String.Trim("  x  "));
print("score: " + String.From(98.5d) + " / " + String.From(true), String.ParseInt("x1"));
print("multi
line");
```

```output
3 8 APPLE,3 a-b-c
2 el a/b/c x
score: 98.5 / 真 null
multi
line
```

## 13. Structs

`struct Name { field: type; other }` (fields separated by `;` or `,`, annotations optional). Call the type to
construct, positionally or by name. Fields left out are `null`, but annotated number, Boolean, string and array
fields must be given. Fields are read and written with `.`, including compound assignment and `++`. Unknown fields
are errors. Structs declared in a module are exported with it.

```aqua
struct Node { value: int; next: Node }
var list = Node(1, Node(2, Node(value: 3)));
var sum = 0;
for (var at = list; at != null; at = at.next) { sum += at.value; }
print(sum, typeOf(list), list.next.value);

struct Account { owner: string; balance: double; tags }
var acct = Account(owner: "ann", balance: 10.0d);
acct.balance *= 1.5d;
acct.tags = ["vip"];
print(acct);
var make = Account;
print(typeOf(make), make("bob", 1.0d).owner);
```

```output
6 Node 2
Account{owner: ann, balance: 15, tags: [vip]}
type bob
```

## 14. Modules and imports

- `import(name)` / `匯入(name)` takes a **string**:
  - a script path relative to the importing file: `import("lib/tools.aqua")` returns a `module` whose top-level
    variables, functions and struct types are its members (`tools.greet("x")`, `tools.Pair(1, 2)`, `tools.counter`);
  - a bundled or Bottle library module by name: `import("Network")`, `import("Processing")`, `import("Python")`;
  - a version 1 Bottle library script: `import("/bottles/NAME/main.aqua")`.
- Each `import` of a script runs it again and creates an independent instance with its own globals. Import shared
  modules once in `main.aqua` and pass the module value to others.
- Inside a module, `currWorkingDir` is that module's folder (no trailing slash); use it to build paths to files next
  to the script.
- `aqua run main.aqua` finds scripts named by string literals. `aqua build` needs them listed:
  `aqua build main.aqua lib/tools.aqua -o app.wasm`. A computed path (`import(dir + "/x.aqua")`) must always be listed.

```aqua
# fragment: lib/geometry.aqua
var scale = 2;
var area = function(w, h) { return w * h * scale; };
struct Size { w; h }
```

```aqua
# fragment: main.aqua
var geo = import("lib/geometry.aqua");
print(geo.area(3, 4), geo.Size(1, 2), geo.scale);   # 24 Size{w: 1, h: 2} 2
```

## 15. Async functions and promises

- `async function(...) { ... }` / `非同步 函式(...) { ... }` returns a promise at once; the body runs as a task on the
  runtime scheduler (single-threaded, cooperative).
- `await value` / `等待 值` waits for a promise inside an async function or at the top level of the entry script.
  Inside a normal function it is a compile error. Awaiting a non-promise returns the value.
- A runtime error inside an async function rejects its promise; `await` of a rejected promise fails the awaiting code
  with that message. An unhandled rejection is reported at exit (`Unhandled promise rejection: ...`) with exit code 1.
- `Promise` / `承諾` (global): `New(function(resolve, reject))`, `Resolve(v?)`, `Reject(r?)`, `All(array)`,
  `AllSettled(array)`, `Race(array)`, `Any(array)`, `Delay(ms, value?)`, `Timeout(promise, ms, reason?)`,
  `IsPromise(v)`; on a promise: `Then(onFulfilled, onRejected?)`, `Catch(f)`, `Finally(f)`, `State`
  (`"pending"`/`"fulfilled"`/`"rejected"`), `Value`, `Reason`, `IsDone`.
- Library functions that wait (network) let other tasks run; their `...Async` variants (`GetAsync`) return promises.
- The program ends when the script finished and only awaits of promises nobody can settle remain.

```aqua
var load = async function(name, ms) {
    await Promise.Delay(ms);
    return name + " ready";
};
var both = await Promise.All([load("a", 30), load("b", 10)]);
print(both);
var failing = async function() { var zero = 0; return 1 / zero; };
print(await failing().Catch(function(message) { return "caught: " + message; }));
var results = await Promise.AllSettled([load("x", 1), failing()]);
print(results[0]["status"], results[1]["status"]);
var p = Promise.New(function(resolve, reject) { resolve(42); });
print(await p, p.State);
```

```output
[a ready, b ready]
caught: Integer division by zero / 整數不能除以零。
fulfilled rejected
42 fulfilled
```

## 16. Threads

`Thread` / `執行緒` runs a function on an isolated worker (OS thread on desktop/Android, Web Worker on the web).
Arguments and captures are copied; results come back as copies (numbers, Booleans, strings, null, arrays, hashes,
structs; no functions or library objects). Workers can only compute: no UI, network, files or imports inside them.

| English | 繁體中文 | |
| --- | --- | --- |
| `Start(function, args?)` | `啟動` | returns a handle |
| `Wait(handle, timeoutMs?)` | `等待` | true when finished, false on timeout (`-1` waits forever, `0` polls) |
| `Result(handle)` | `結果` | waits, returns a copy of the result; worker errors propagate |
| `Cancel(handle)` / `Close(handle, timeoutMs?)` | `取消` / `關閉` | cooperative cancel / cancel, join and release (at most 64 open) |
| `IsDone(handle)`, `Sleep(ms)`, `IsCancellationRequested()` | `是否完成`, `休眠`, `是否要求取消` | |

```aqua
var work = function(n) { var s = 0; for (var i = 1; i <= n; i++) { s += i; } return s; };
var jobs = [Thread.Start(work, [100]), Thread.Start(work, [1000])];
print(Thread.Result(jobs[0]), Thread.Result(jobs[1]));
Thread.Close(jobs[0]);
var closed = Thread.Close(jobs[1]);   # a bare Thread.Close(...) as the last statement would make aqua run print 真
```

```output
5050 500500
```

## 17. Core global functions

| English | 繁體中文 | Description |
| --- | --- | --- |
| `print(v, ...)` | `印出` | prints the values separated by spaces, then a newline |
| `len(v)` | `長度` | string length (UTF-16 units), array length, hash entry count |
| `last(a)`, `rest(a)`, `push(a, v)` | `最後一個`, `其餘`, `加入` | see section 10 |
| `typeOf(v)` | `型別` | type tag (section 5) |
| `arityOf(f)` | `參數個數` | declared parameter count |
| `int(x)`, `float(x)`, `double(x)` | `整數`, `浮點數`, `雙精度` | numeric conversions |
| `import(s)` | `匯入` | section 14 |
| `currentTime()` | `目前時間` | monotonic double milliseconds (subtract two readings) |
| `currentDate()` | `目前日期` | local date `yyyy-MM-dd` |
| `isOSWindows()`, `isOSLinux()`, `isOSMacOS()`, `isOSAndroid()`, `isWeb()` | `是Windows`, `是Linux`, `是MacOS`, `是Android`, `是網頁` | platform checks |
| `execFile(path, argsArray)` | `執行檔案` | runs a program synchronously (desktop); prefer the `Terminal` library |
| `currWorkingDir` (a property, no parentheses) | `目前工作目錄` | folder of the executing script |
| `floodFill(...)` | `區域填色` | section 10 |
| `Array`, `Hash`, `String`, `Thread`, `Promise` | `陣列`, `雜湊`, `字串`, `執行緒`, `承諾` | built-in modules |

There is no math module in the core: use `import("Processing")` (`sqrt`, `pow`, `abs`, `floor`, `ceil`, `round`,
`sin`, `cos`, `tan`, `atan2`, `exp`, `log`, `min`, `max`, `constrain`, `map`, `lerp`, `dist`, `random`,
`randomSeed`, `noise`, `PI`, ...; importing it opens no window) or `import("GLM")` (`Sqrt`, `Sin`, `Cos`, `PI`, vector
and matrix helpers). There is no `exit()`; return from your top-level code, or end with an error for exit code 1.

```aqua
var P = import("Processing");
print(P.sqrt(2), P.pow(2, 10), P.floor(2.7d), P.round(2.5d), P.abs(-3), P.min(3, 1, 2), P.constrain(15, 0, 10));
print(typeOf(currentTime()), String.Contains(currentDate(), "-"), typeOf(isWeb()));
```

```output
1.4142135623730951 1024 2 3 3 1 10
double 真 boolean
```

## 18. Errors and error messages

**Compile errors** (syntax, type, misplaced `await`) go to standard error as `<file path>: <message>`, often several
lines for one mistake (read the first). The build exits with 1 and nothing runs. Common ones:

| Message | Usual cause |
| --- | --- |
| `No prefix parse function for ILLEGAL(0.5;) found.` | bare decimal literal: write `0.5d` |
| `No prefix parse function for ILLEGAL(?) found.` | `?:` does not exist; use `if` as an expression |
| `No prefix parse function for ;(;) found.` after `x--` | no `--`; write `x -= 1` |
| `No prefix parse function for ,(,) found.` | `var a = 1, b = 2;`; declare separately |
| `No prefix parse function for ILLEGAL(') found.` | single-quoted string |
| `Expected next token to be :, got ; instead.` | a `while` loop (parsed as a call followed by a block) |
| `Expected next token to be (, got IDENT instead.` | `function name() {}`; write `var name = function() {};` |
| `Expected next token to be ), got = instead.` | default parameter value |
| `Expected next token to be IDENT, got IF instead.` | keyword used as a name |
| `Type mismatch: n expects 整數 int, got 字串 string.` | annotation violated |
| `S.a (整數 int) needs a value.` | typed struct field left out |
| `await is only valid inside an async function or at the top level of a script.` | await in a normal function |

**Runtime errors** print `ERROR: <message>` to standard output, stop the program (no later statements run) and exit
with code 1. Messages carry no line numbers: add `print` statements to locate them. Common ones:

| Message | Cause |
| --- | --- |
| `Identifier not found: name` | undeclared name (also: library module not imported, e.g. `Application` without `import`) |
| `Type mismatch: STRING + INTEGER` | `+` between string and number; use `String.From` |
| `Unknown operator: STRING < STRING`, `ARRAY + ARRAY` | unsupported operator for those types |
| `Type mismatch: INTEGER && BOOLEAN` | non-Boolean operand of `&&`/`\|\|` |
| `Cannot access member of HASH` / `of NULL` | `h.key` on a hash, or a member of `null` |
| `Not a function: INTEGER` | calling a non-function |
| `Function expects 2 arguments, got 1.` | wrong argument count |
| `Integer division by zero / 整數不能除以零。` | integer `/` or `%` by 0 |
| `Array index is out of bounds / 陣列索引超出範圍。` | typed array read/write outside its range |
| `Array assignment index is out of bounds.` | `a[i] = v` past the end; use `Array.Append` |
| `Indexed assignment requires an array or hash.` | `s[i] = ...` on a string |
| `Module not found: X` / `匯入: Packaged script not found: lib/x.aqua` | bad library name / script not compiled in |
| `Files.SaveText expects 3 to 4 arguments, got 2.` | library signature check (`Module.Function expects ...`) |
| `Network.ParseURL: InvalidArgument: Invalid URL / 網址無效。` | library errors: `Module.Function: Kind: message` |

Ways to handle failures without crashing:

- Check before acting: `if (x != null && ...)`, `Hash.Has`, `typeOf(v) == "string"`, `Files.Stat(r).exists`.
- Promises: `await f().Catch(function(message) { ... })` or `Promise.AllSettled`.
- `Network.Try(function() { ... })` returns a record with `Success`, `Value`, `Error` (`Error.Kind`, `Error.Message`)
  for any code that raises a library error. Network responses report failures as data (`Success`, `Status`, `Error`)
  instead of raising.
- Bridges (Python/Java) return `{"ok": ..., "value": ..., "error": ...}` records.

## 19. Bundled libraries

Import by name; English and Chinese names load the same module. Each library's `README.md` and `docs/` in the
package's `libraries/<name>/` folder are the full reference; the tables below are summaries. Platforms: W = Windows,
L = Linux, A = Android, Web.

| Library folder | Modules (import names) | Platforms |
| --- | --- | --- |
| `application` | `Files`, `Paths`, `Images`, `Serialization`/`序列化`, `Storage`, `Clipboard`, `Application`, `Tasks`, `TextEdit` | W L A Web |
| `network` | `Network` / `網路` | W L A (Web: HTTP and WebSocket clients) |
| `terminal` | `Terminal` / `終端機` | W L A |
| `audio` | `Audio` / `音訊` | W L A Web |
| `camera` | `Camera` / `相機`, `Webcam` / `攝影機` | W L A Web |
| `graphics` | `Processing` / `繪圖`, `WGPU`, `GLM`, `TextInput`, `Window`, `Fonts`, `Models` / `模型`, `PBR`, `GPUMath` / `GPU數學` | W L A Web (WebGPU) |
| `opengl` | `GLFW`, `GLAD`, `GL`, `STBImage` | W L |
| `interaction` | `Interaction` / `互動`, `ScrollView`, `Navigation`, `ContextMenu`, `Focus`, `HitTest` | W L A Web |
| `input` | `AndroidInput` | A |
| `onnx` | `ONNX` / `推論引擎`, `MediaPipe` / `姿態追蹤` | W L A Web |
| `webframe` | `WebFrame` / `網頁框架` | Web (others report unsupported) |

### Files, Paths, Serialization, Storage, Application (library `application`)

- Failures raise `Module.Function: Kind: message` with kinds `Unsupported`, `PermissionDenied`, `Unavailable`,
  `Cancelled`, `InvalidData`, `LimitExceeded`, `NotFound`, `Conflict`.
- `Files.Resolve(path)` turns a path into a resource; the others take resources: `Stat(r)` (`exists`, `length`,
  `kind` 0 file / 1 directory, `modified`), `ReadText(r, "utf-8")`, `SaveText(r, text, "utf-8", requireAtomic?)`,
  `ReadBytes(r)`, `SaveBytes(r, bytes)`, `Enumerate(dir)`, `CreateDirectory(dir)`, `Delete(r, recursive?)`,
  `Copy(a, b, overwrite?)`, `Move(a, b, overwrite?)`, `Open(r, mode)` (stream: `Read(n)`, `Write(bytes)`, `Seek`,
  `Flush`, `Close`), `Temporary(isDirectory)`, `ReadAssetText(path, "utf-8")`, `ReadAssetBytes(path)` (files packaged
  with `--assets`).
- `Paths`: `Join(array)`, `Name`, `Extension`, `Parent`, `Normalize`, `Relative(from, to)`.
- `Serialization`: `Json(value)` (UTF-8, compact), `ParseJson(text)` (`1` becomes an integer, `2.5` a double),
  `Binary(value, schema)`/`ParseBinary(bytes)` (AQD1 documents), `Compress(bytes, format, level?)` and
  `Decompress(bytes, format)` with formats `Gzip`, `Zlib`, `Deflate`, `Brotli`, `Archive(hash of name → resource)` /
  `ReadArchive(bytes)` (ZIP). Bytes are arrays of integers 0..255.
- `Storage`: `Read(key)`, `Write(key, bytes)`, `Delete(key)`, `GetPreference(key)`, `SetPreference(key, value)`,
  `SettingsDirectory()`, `DataDirectory()`.
- `Application`: `Environment(name)` (environment variable or null), `LaunchFiles()` (resources for the command-line
  arguments given after the app), `OpenFiles(options)`, `SaveFile(options)`, `PickDirectory()` (dialogs),
  `Capabilities()`.
- `Images`: `Load(resource, frame)`, `Decode(bytes, frame)`, `Encode(image, format, options)`, `Save`, `Create`,
  `Inspect`, `Formats()`; PNG, JPEG, GIF, BMP, TIFF.
- `Clipboard`: `ReadText()`, `WriteText(s)`, `ReadImage()`, `WriteImage(image)`. `TextEdit`: grapheme-aware editor
  state for custom text fields. `Tasks`: cancellation tokens.

```aqua
var Files = import("Files");
var Paths = import("Paths");
var Serialization = import("Serialization");
var file = Files.Resolve(currWorkingDir + "/agent-ref-demo.json");
Files.SaveText(file, Serialization.Json({"items": [1, 2.5d, "three"], "ok": true}), "utf-8");
var doc = Serialization.ParseJson(Files.ReadText(file, "utf-8"));
print(doc["items"], typeOf(doc["items"][0]), typeOf(doc["items"][1]), doc["ok"]);
print(Files.Stat(file).exists, Paths.Extension("a/b.json"), Paths.Join(["a", "b", "c.txt"]));
Files.Delete(file);
print(Files.Stat(file).exists, Serialization.ParseJson("3.25") * 2);
```

```output
[1, 2.5, three] integer double 真
真 .json a/b/c.txt
假 6.5
```

### Network (library `network`)

HTTP(S) client (keep-alive, cookies, redirects, gzip), streamed responses (SSE), WebSocket client and server, TCP,
TLS, UDP, DNS, and an HTTP(S) server with Go-style routes. The web target supports only the HTTP and WebSocket
clients. Chinese aliases exist for every name (`網路.取得`, `回應.成功`, ...).

- Module: `Get(url, options?)`, `Post(url, body, options?)`, `Request(method, url, options?)`, each with an `...Async`
  promise variant; `CreateClient(options?)` (same methods plus `ClearCookies`, `Close`), `StartRequest`, `StartStream`
  (`ReadLines()`, `Read()`, `Status()`, `IsDone()`, `Poll()`, `Wait()`, `Cancel()`), `ConnectWebSocket(url)`,
  `ConnectTCP(host, port)`, `ConnectTLS`, `ListenTCP`, `OpenUDP`, `Resolve(host)`, `Listen(url)` (server),
  `ListenAndServe(address, routes)`, `ParseURL`, `EncodeQuery(hash)`, `StatusText(code)`, `RandomBytes(n)`,
  `TextToBytes`, `BytesToText`, `Try(function)`, `Supports(feature)`, `Capabilities()`.
- Options (hash): `timeout` (ms, default 10000), `headers` (hash), `body`, `json` (value sent as JSON), `form`,
  `followRedirects`, `maxRedirects`, `maxBodyBytes`, `credentials`.
- Response: `Success` (2xx), `Status` (0 when the connection failed), `Url`, `Body` (text), `Headers`, `Error`
  (`Kind`, `Message`, `Retryable`), `JSON()`, `Bytes()`, `Header(name)`. HTTP errors and connection failures do not
  raise; check `Success`.
- Server: `var server = Network.Listen("http://127.0.0.1:8080");` then `server.Handle("GET /items/{id}", handler)`
  (or `Handle("GET", "/path", handler)`), `Use(middleware)`, `Group(prefix)`, `Static(prefix, folder)`, `CORS(origins)`,
  `Serve()` (blocks; handlers run concurrently), `ServeAsync()`, `Poll(timeout?)`, `Shutdown()`, `Close()`.
  Handlers get `(request, writer)`: request `Method`, `Path`, `Body`, `Headers`, `Param(name)`, `Query(name)`,
  `Header(name)`, `Cookie(name)`, `JSON()`, `Form()`, `FormValue(name)`, `Files()`, `Session()`; writer `Status(code)`,
  `Header(name, value)`, `Text(s)`, `JSON(v)`, `Bytes(b)`, `File(path)`, `Redirect(url, code?)`, `Error(code, msg?)`,
  `NotFound()`, `Event(name, data)` (SSE), `UpgradeWebSocket()`.
- WebSocket: `SendText`, `SendBytes`, `Receive()` (message `Text`, `Data`, `IsText`, `Closed`), `Close()`, plus
  `...Async` variants.

```aqua
var Network = import("Network");
var server = Network.Listen("http://127.0.0.1:18931");
server.Handle("GET /hello/{name}", function(request, writer) {
    writer.JSON({"hello": request.Param("name"), "q": request.Query("q")});
});
server.ServeAsync();
var response = await Network.GetAsync("http://127.0.0.1:18931/hello/ann?q=1");
print(response.Success, response.Status, response.JSON());
var missing = Network.Get("http://127.0.0.1:18931/nothing");
print(missing.Success, missing.Status);
var down = Network.Get("http://127.0.0.1:1/");
print(down.Success, down.Status, down.Error.Kind);
var attempt = Network.Try(function() { return Network.ParseURL("::bad"); });
print(attempt.Success, attempt.Error.Message);
print("stopped", server.Shutdown());
```

```output
真 200 {hello: ann, q: 1}
假 404
假 0 Network
假 Network.ParseURL: InvalidArgument: Invalid URL / 網址無效。
stopped 真
```

### Terminal (library `terminal`)

`Run(executable, argsArray, options?)` starts a program directly (no shell); `RunScript(script, options?)` uses
PowerShell on Windows, `/bin/sh -c` on Linux and Android. Options: `TimeoutMs` (default 60000), `WorkingDirectory`.
The result has `ExitCode`, `StandardOutput`, `StandardError`, `TimedOut`. `IsSupported()` is false on the web.

```aqua
var Terminal = import("Terminal");
if (Terminal.IsSupported()) {
    var result = Terminal.RunScript("Write-Output 'hi from the shell'", {"TimeoutMs": 20000});
    print(result.ExitCode, String.Trim(result.StandardOutput), result.TimedOut);
}
```

```output
0 hi from the shell 假
```

(On Linux the script runs in `sh`; write `echo hi from the shell` there.)

### Processing and graphics (library `graphics`)

`Processing` / `繪圖` is a Processing-style 2D/3D sketch API on WebGPU (desktop, Android, browser). Call
`p.run(setup, draw)`: `setup` calls `p.size(width, height, p.P2D or p.P3D, title?)` once, `draw` runs every frame.
Nearly all Processing functions exist with their usual names (`background`, `fill`, `stroke`, `noStroke`, `rect`,
`ellipse`, `circle`, `line`, `triangle`, `beginShape`/`vertex`/`endShape`, `text`, `textSize`, `image`, `loadImage`,
`createGraphics`, `push`/`pop`, `translate`, `rotate`, `scale`, `camera`, `lights`, `box`, `sphere`, `loadShader`,
`createVector`, `random`, `noise`, `millis`, `frameRate`, `noLoop`, `loop`, `exit`, `save`, `saveFrame`, ...).
State is read through properties: `p.width`, `p.height`, `p.frameCount`, `p.mouseX`, `p.mouseY`, `p.isMousePressed`,
`p.key`, `p.keyCode`, `p.isKeyPressed`. Events are registered with `p.on(name, f)` for `mousePressed`,
`mouseReleased`, `mouseClicked`, `mouseMoved`, `mouseDragged`, `mouseWheel`, `keyPressed`, `keyReleased`, `keyTyped`,
`windowResized`, `textInput`, `compositionStarted`/`Updated`/`Ended` and `error`. Constants are members (`p.CENTER`, `p.P3D`, `p.PI`). Remember the `d` suffix on
decimal literals passed to it. Headless runs: set `AQUARIUS_GRAPHICS_FRAMES` and `AQUARIUS_GRAPHICS_CAPTURE`.

```aqua
# fragment: needs a window and a GPU
var p = import("Processing");
var Interaction = import("Interaction");
var clicks = 0;
var setup = function() {
    p.size(320, 200, p.P2D, "demo");
    var session = Interaction.Attach(p.platformWindow);
    session.On("click", function(event) { clicks += 1; return true; });
};
var draw = function() {
    p.background(20, 24, 32);
    p.noStroke();
    p.fill(240, 180, 40);
    p.circle(160 + 60 * p.cos(p.frameCount * 0.05d), 100, 40);
    p.fill(255);
    p.textSize(16);
    p.text("clicks: " + String.From(clicks), 10, 24);
};
p.run(setup, draw);
```

Other graphics modules: `WGPU` (raw WebGPU: `CreateDevice()`, `CreateWindow(w, h, title)`, buffers, WGSL shaders,
compute `Dispatch`, render targets), `GLM` (vectors/matrices: `Perspective`, `LookAt`, `Rotate`, `Multiply`, ...),
`TextInput` (IME text entry), `Window` (window integration: cursors, file drops, close interception), `Fonts`
(enumerate and measure system fonts), and the Aquarius-source modules `Models` (OBJ/MTL loading), `PBR` (physically
based rendering in WGSL) and `GPUMath` (GPU array math). See `libraries/graphics/README.md` and its `docs/`.

### Interaction, ScrollView, Navigation, ContextMenu, Focus, HitTest (library `interaction`)

Native-feeling input for interfaces you draw yourself. `Interaction.Attach(window)` (a `p.platformWindow` or
`wgpuWindow.PlatformWindow`) returns a session; `session.On(type, handler)` with types `press`, `release`, `click`
(with `count`), `move`, `dragStart`, `drag`, `dragEnd`, `longPress`, `pinch`, `contextMenu`, `scroll`, `zoom`, `key`,
`text`, `composition`, `command` (`copy`, `paste`, `undo`, ...), `back`, `resize`, ... Handlers get an event record
(`x`, `y`, `dx`, `dy`, `button`, `count`, `key`, `command`, `modifiers`, ...) and return `true` to consume it.
`Interaction.Conventions()` gives platform timings and style (double-click time, drag threshold, dark mode). The
Aquarius modules add scroll views, a back stack, context menus, tab focus and layered hit testing. Read
`libraries/interaction/README.md` and the package's `examples/` before building a UI.

### Audio, Camera, ONNX, OpenGL, WebFrame, AndroidInput

- `Audio`: `var device = Audio.CreateDevice(); var tone = device.CreateTone(440, 0.5d); device.Start(); tone.Play();`.
  Sounds: `Load(path)` (WAV/FLAC/MP3; packaged assets or files), `Play`, `Pause`, `Stop`, `SetVolume(0..1)`,
  `SetPan(-1..1)`, `SetPitch`, `SetLooping`, `Seek`, `IsPlaying`. `CreateOfflineDevice()` + `Render(frames)` renders
  PCM without hardware. Format: 48 kHz stereo float.
- `Camera` / `Webcam`: `IsSupported()`, `Enumerate()`, `Open(...)` (four arguments) → session `Capture()` → frame
  (`寬度`, `高度`, `影像`, `GetPixels()`), `Dispose()`. Read `libraries/camera/README.md` for the arguments.
- `ONNX`: `CreateSession(model, options?)` → session `GetInputs()`, `GetOutputs()`, `Run(...)`, `Dispose()`;
  `CreateTensor(...)`; provider constants `ONNX.CPU`, `ONNX.CUDA`, `ONNX.WebGPU`, `ONNX.Auto`, ...;
  `MediaPipe.CreatePoseTracker(...)` for pose landmarks. Read `libraries/onnx/README.md` for the argument forms.
- `GLFW`, `GLAD`, `GL`, `STBImage`: desktop OpenGL 3.3 with C-like names (`GL.glClear(GL.GL_COLOR_BUFFER_BIT)`).
- `WebFrame`: embeds iframes on the web target only (`Supported()` false elsewhere).
- `AndroidInput`: soft keyboard, vibration and back handling on Android.

## 20. Bottle projects and external libraries

A project is a folder with `aqua.toml`. `aqua new MyApp` writes:

```toml
manifest_version = 2

[project]
name = "MyApp"
version = "0.1.0"
entry = "main.aqua"
sources = ["main.aqua"]            # list every script (files or folders)
platforms = ["windows", "linux", "web", "android"]

[dependencies]
# Aqua_SQLite = { path = "../Aqua_SQLite/aqua.toml", version = ">= 2.0, < 3.0" }
# Aqua_WebSocket = { git = "https://github.com/OWNER/REPOSITORY.git", rev = "main", version = "3.0.0" }

[[assets]]
source = "assets"                  # files the app reads at run time (Files.ReadAssetText, Audio.Load, ...)
```

Optional keys: `libraries = ["name"]` in `[project]` for libraries imported by computed names, `aquarius = ">=
0.1.6"`, `[desktop] hide_terminal = true`, `[onnx] cuda = true`. Version requirements use `=`, `>`, `>=`, `<`,
`<=` joined by commas (AND); no `^`, `~` or wildcards.

```text
aqua new MyApp                                    # creates MyApp/aqua.toml and MyApp/main.aqua
aqua build MyApp                                  # MyApp/dist/MyApp.wasm (resolves and caches dependencies)
aqua run MyApp                                    # build and run
aqua build MyApp --target windows -o out/MyApp.exe
aqua bottle install ../SomeLibrary --project MyApp --version ">= 1.0, < 2.0"
aqua bottle install https://github.com/OWNER/REPO.git --rev v1.2.0 --project MyApp   # HTTPS GitHub only; needs git
aqua bottle restore --project MyApp [--locked] [--offline]
aqua bottle update --project MyApp                # re-read sources after a library changed
aqua bottle list --project MyApp
aqua bottle cache path | list | remove NAME VERSION
```

- The cache (`~/.aquarius/bottles`, or `AQUA_BOTTLE_HOME`) is immutable per name and version: after editing a local
  library without bumping its version, run `aqua bottle cache remove NAME VERSION` and delete `aqua.lock.json`, or the
  old snapshot keeps being used (or the build fails with a content mismatch).
- Do not combine a project build with `--root`, `--entry` or `--assets`; the manifest decides those.
- Libraries with platform-specific native files need a platform: `aqua build . --platform windows -o app.wasm`.
- Import a version 2 library by its module name (`import("Python")`); a version 1 library by path
  (`import("/bottles/NAME/main.aqua")`). Read the library's README in its source folder or in the cache
  (`aqua bottle cache path`).
- `aqua run app.wasm` inside a project passes the project's native libraries to the runtime automatically. Exported
  executables put them in `libraries/` next to the `.exe`; ship the whole output folder.

### Known external libraries

These live outside the package (the Aquarius-Language GitHub organization or the author's machine). Check that the
version you get is a `manifest_version = 2` library (module-name imports); older version 1 releases may not load in
this runtime.

| Library | Import | What it does | Platforms |
| --- | --- | --- | --- |
| `Aqua_PyBridging` 2.0 | `Python` / `Python橋接` | in-process CPython 3.10–3.14, two-way calls | W L |
| `Aqua_JBridging` 2.1 | `Java` / `Java橋接` | in-process JDK 17+ through JNI | W |
| `Aqua_SQLite` 2.0 | `SQLite` / `資料庫` | SQLite database | W L A Web |
| `Aqua_WebSocket` 3.0 | `AquaWebSocket` | WebSocket layer on Network | W L A Web |
| `Aqua_JSON-RPC` 1.0 | `AquaJSONRPC` | JSON-RPC 2.0 | W L A Web |
| `Aqua_CRDT`, `Aqua_WebRTC` | see READMEs | collaborative data, WebRTC | W L A Web |
| `Aqua_OpenCV` 3.0 | `OpenCV` / `cv` | cv2-style computer vision | W L A Web |
| `Aqua_JoltPhysics` 2.0 | `JoltPhysics` / `剛體物理` | rigid-body physics | W L A Web |
| `Aqua_Filament` 2.0 | `Filament` / `即時算繪` | Filament PBR renderer | W L Web |
| `Aqua_YOLOX` 1.0 | `YOLOX` / `物件偵測` | object detection on ONNX | W L A Web |
| `Aqua_Blender5` 1.0 | `Blender` / `布蘭德` | Blender `bpy` through PyBridging | W L |
| `Aqua_LLM_API` 1.0 | see README | LLM HTTP APIs | W L A Web |

### PyBridging (Python)

Add the dependency (`Aqua_PyBridging = { path = "<folder>/aqua.toml", version = "2.0.0" }`), build with
`--platform windows` (or `linux`), and point `Start` at a CPython folder (one holding `python3XY.dll` on Windows, or
`lib/libpython3.Y.so` on Linux, such as the python.org NuGet package or python-build-standalone). `PATH` and
`PYTHON*` variables are never used. Every operation returns a record: check `["ok"]`, then use `["value"]`; failures
add `["error"]`, `["exceptionClass"]`, `["stackTrace"]`.

```aqua
# fragment: needs the Aqua_PyBridging dependency and a portable Python
var Python = import("Python");
var started = Python.Start({"python": "python", "baseDirectory": currWorkingDir, "paths": [currWorkingDir + "/scripts"]});
if (started["ok"]) {
    print(Python.Eval("6 * 7")["value"]);                                   # 42
    print(Python.CallModule("math", "sqrt", [2])["value"]);                 # 1.4142135623730951
    var mapped = Python.CallModule("builtins", "map", [function(x) { return x * 2; }, [1, 2, 3]])["value"];
    print(Python.InvokeValue(Python.Eval("list")["value"], [mapped])["value"]);   # [2, 4, 6]
    Python.Expose("greet", function(name) { return "hi " + name; });
    print(Python.EvalValue("__import__('aquarius').greet('py')")["value"]);  # hi py
    var failed = Python.Eval("1/0");
    print(failed["ok"], failed["exceptionClass"]);                         # 假 ZeroDivisionError
}
Python.Dispose();
```

API (English / 中文): `Start`/`啟動`, `Info`, `Import(name)`, `Eval`/`EvalValue`, `Exec(code)`, `Scope()`,
`EvalIn`/`ExecIn(scope, code)`, `Call`/`CallValue(target, method, args)`, `Invoke`/`InvokeValue(callable, args)`,
`CallModule`/`CallModuleValue(module, function, args)`, `GetAttr`/`GetAttrValue`, `SetAttr`, `HasAttr`,
`GetItem`/`GetItemValue`, `SetItem`, `DelItem`, `Length`, `ToString`, `Repr`, `TypeName`, `Dir`, `ToValue(handle)`,
`FromValue(value)`, `Release(handle)`, `Clear()`, `Args(positional, keywords)` (keyword arguments), `Expose(name, f)`,
`Callback`, `Pump()` (run callbacks queued by Python threads), `Throw(type, message)`, `Long`, `Decimal`, `Bytes`,
`Tuple`, `IsHandle`, `AssetFile(path)`, `Shutdown()` (no restart), `Dispose()`. `...Value` variants copy the result into
Aquarius data instead of returning a handle (`{"$py": id, "type": ...}`). Ship the Python folder next to the exported
executable.

### JBridging (Java)

Add `Aqua_JBridging = { path = "<folder>/aqua.toml", version = "2.1.0" }` (Windows x64 only) and start a JDK 17+
folder that contains `bin/server/jvm.dll` (an empty `jdk` uses `JAVA_HOME`). Parameter types are always explicit.

```aqua
# fragment: needs the Aqua_JBridging dependency and a JDK
var Java = import("Java");
var started = Java.Start({"jdk": "C:/Java/jdk-21", "classpath": [], "options": ["-Xmx256m"]});
if (started["ok"]) {
    print(Java.CallStatic("java.lang.Math", "abs", ["int"], [-42])["value"]);         # 42
    var builder = Java.New("java.lang.StringBuilder", ["java.lang.String"], ["你好"])["value"];
    Java.Call(builder, "append", ["java.lang.String"], [" Java"]);
    print(Java.ToString(builder)["value"]);                                         # 你好 Java
    Java.Release(builder);
} else {
    print(started["error"]);
}
Java.Dispose();
```

API: `Start` (`jdk`, `baseDirectory`, `classpath`, `options`), `Info`, `Class`, `New(class, types, args)`,
`CallStatic(class, method, types, args)`, `Call(handle, method, types, args)`, `GetStatic`/`SetStatic`,
`GetField`/`SetField`, `Array(componentType, values)`, `ArrayGet`/`ArraySet`/`ArrayLength`/`ArraySlice`, `IsInstance`,
`Batch(requests)`, `Running()`, `ToValue`, `ToString`, `Release`, `Clear`, `Long(text)`, `Decimal(text)`, `AssetFile`,
`Shutdown()`, `Dispose()`. Java varargs take one array argument (`types = ["java.lang.String[]"]`).

### Writing your own library

An Aquarius-only library is a folder with `aqua.toml` (`manifest_version = 2`, a `[library]` table with
`name`/`version`/`platforms`, and `[[modules]]` naming its `.aqua` files and import names); create one with
`aqua new MyLib --type library`. Native parts (C17, C++20 or Rust with a C ABI) use the SDK in the package's `sdk/`
folder (`sdk/README.md`, `sdk/example/`) and are built with `aqua library build <folder> --targets ...`, which needs
the pinned LLVM toolchain (`scripts/install-toolchain.ps1`).

## 21. Finding the exact API

The package itself is the authoritative reference:

- **Every native module, member and argument count**, as JSON:
  `<aqua>/runtime/windows-x64/aquarius.exe --catalog` (or `runtime/linux-x64/aquarius --catalog`). Each member lists
  `names` (English and Chinese), `kind` (`function`, `property`, `constant`) and `signature` (`v` any value, `s`
  string, `i` integer, `n` number, `b` Boolean, `a` array, `h` hash, `f` function, `o` object; `|` separates required
  from optional arguments, `*` repeats). Add `--library path/to/aqua_x.dll` to include an external native library.
- **Library guides**: `<aqua>/libraries/<name>/README.md` and `<aqua>/libraries/<name>/docs/*.md`.
- **Aquarius-source modules** (Models, PBR, GPUMath, ScrollView, Navigation, ContextMenu, Focus, HitTest):
  `<aqua>/libraries/<name>/aqua/*.aqua` (read the source).
- **Examples**: `<aqua>/examples/` (`starship_expedition` covers most of the language; `async`, `threading`,
  `network_lab`, `processing_showcase`, `wgpu_*`, `opengl_cube`, `application_libraries`, `type_declarations`,
  `bottle_showcase`, `external_library`). Run one with `aqua run <aqua>/examples/increment.aqua`.

## 22. Build targets and deployment

| Target | Command | Output and requirements |
| --- | --- | --- |
| `wasm` (default) | `aqua build main.aqua lib/x.aqua --assets assets -o app.wasm` | one portable `.wasm` with modules and assets; run with `aqua run app.wasm` |
| `windows` | `aqua build app.wasm --target windows -o out/app.exe` | `app.exe` plus the runtime DLLs and `licenses/` beside it; ship the whole folder. `--hide-terminal true` for a GUI app without a console |
| `linux` | `aqua build app.wasm --target linux -o out/app` | ELF executable plus shared libraries (needs the `linux-x64` pack) |
| `web` | `aqua build app.wasm --target web -o site` | static site (`index.html`, `app.mjs`, `program.wasm`, `runtime/`, `glue/`); serve over `http://localhost` or HTTPS (not `file://`); graphics need a WebGPU browser |
| `android` | `aqua build app.wasm --target android -o app.apk` | signed APK (arm64 and x86_64); needs `ANDROID_HOME` (build-tools, a platform) and a JDK |

Sources can be given directly instead of a `.wasm` (`aqua build main.aqua --target web -o site`). On the web, the
`Terminal`, TCP/UDP and HTTP server features report `Unsupported`; check `Terminal.IsSupported()`,
`Network.Supports("tcp")` or `isWeb()` and fall back.

## 23. Pitfalls checklist

Before running a program, check:

1. Every decimal literal has `d` or `f` (`0.5d`, `1.0f`); no `1e3`.
2. No `while`, `?:`, `--`, `switch`, `try`, `class`, `=>`, `let`, `const`, `function name()`, default parameters,
   single quotes, `//` comments.
3. Mixed `&&`/`||` chains are parenthesized; their operands are Booleans (not numbers or null).
4. Conditions: `0` and `""` are true; test `x != null`, `len(s) > 0`, `n != 0` explicitly.
5. Numbers are converted with `String.From(n)` before joining with strings; strings are compared with `==` only.
6. Integer division is intended, or one operand is a double (`7.0d / 2`, `double(n) / d`).
7. Hash values are read with `h["key"]`, never `h.key`; `Hash.Keys(h)` for loops.
8. `push(a, v)` returns a new array; use `Array.Append(a, v)` to grow `a` in place. `a[i] = v` cannot grow an array.
9. Newlines in strings come from multi-line literals or `String.FromCode(10)`, not `"\n"`.
10. Library modules are imported (`var Files = import("Files");`) before use; globals are only those in section 17.
11. Every script imported by a computed path is listed in `aqua build` or the project's `sources`.
12. Calls pass exactly the declared number of arguments.
13. `await` appears only in async functions or at the top level.
14. Struct fields annotated with number, string, Boolean or array types are given at construction.
15. Output with `真`/`假` is expected for Booleans (`print(true)` prints `真`).
16. The script does not end with a bare expression or a call that returns a value (`Thread.Close(job);`,
    `server.Shutdown();`) unless you want `aqua run` to print that value.
