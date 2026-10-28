# 變更紀錄 / Changelog

One version covers the language, the compiler, the CLI, the language server and the VS Code extension; see
[versioning](docs/versioning.md).

## 0.1.5

Programs keep their meaning and output; they run faster.

Performance:

- Typed number and Boolean arrays are packed: an `整數[]` / `int[]` stores 4 bytes per element, `浮點數[]` / `float[]` 4,
  `雙精度[]` / `double[]` 8 and `布林[]` / `bool[]` 1, instead of a 16-byte value each.
- Fully typed functions and native loops run `陣列.追加` / `Array.Append` natively, and `陣列.建立` / `Array.Make` written
  straight into a typed array annotation builds the packed array directly.
- Fix: a pending garbage collection that stopped being due could keep every native loop on the slow path.

Runtime and libraries:

- Applications declare value ABI 3 (packed arrays). The runtime still runs applications built by 0.1.4; a 0.1.4 runtime
  refuses 0.1.5 applications with a clear message instead of misreading their arrays.
- The SDK's `aqua_array_set`, `aqua_array_push` and `aqua_array_write_*` return false for a value a typed array cannot
  hold (an integer still widens into a float or double array), instead of breaking the array's type.

## 0.1.4

Language:

- `空值` / `null` literal; struct-typed variables, parameters, results and fields accept it.
- `繼續` / `continue`.
- `字串[]` / `string[]` typed arrays; integers written to `浮點數[]` / `雙精度[]` elements widen like typed variables.
- Compound assignment and `++` on array elements, hash entries and struct fields (`表[i] += 1`, `計數[鍵]++`, `點.x++`),
  and writes through members (`物件.清單[0] = 值`).
- Loops may leave out any of their three parts (`迴圈 (;;)`); closures made in a loop keep their own iteration's value.
- `\"` and `\\` in strings; `-2147483648` literal.
- Fixes: a statement starting with `(`, `[`, `-` or `++` after an `如果` / `迴圈` block is its own statement; deep
  nesting is one parser error instead of a crash; duplicate parameters and invalid assignment targets are compile errors.
- `null`, `空值`, `continue` and `繼續` are now reserved words.

Tools:

- One version for everything: `aqua --version`, the language server's `serverInfo`, the VS Code extension (which warns
  about a server of another release) and `aquarius = ">= 0.1.4"` in `aqua.toml` projects and libraries.
- The language server and the editor grammar know the new keywords and write forms.

## 0.1.3

Typed annotations, number types, structs and `陣列` / `array`; the native tier and the desktop runtime JIT; bilingual
signature help in the language server.
