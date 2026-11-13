# Runtime errors: where they happened / 執行期錯誤的位置

A runtime error ends the program (or the callback or import it happened in) with its message, the place in the source,
the source line with the failing part underlined, and the call stack:

```text
ERROR: Module member not found: Length
  --> .aquarius/AquariusEngine/aqua/Display.aqua:58:32
   |
58 |     for (var i = 0; i < String.Length(text); i++) {
   |                                ~~~~~~
  at _slug (.aquarius/AquariusEngine/aqua/Display.aqua:58)
  at SettingsPath (.aquarius/AquariusEngine/aqua/Display.aqua:71)
  at Attach (.aquarius/AquariusEngine/aqua/Display.aqua:90)
  at Start (.aquarius/AquariusEngine/aqua/Engine.aqua:931)
  at _run (.aquarius/AquariusEngine/aqua/Engine.aqua:999)
  at Run (.aquarius/AquariusEngine/aqua/Engine.aqua:1092)
  at <top level> (main.aqua:8)
```

(A game whose engine package had `String.Length`, which does not exist, put back in a copy; a package's modules are
under `.aquarius/<package>/` in the application.)

- **The first line is unchanged:** `ERROR: <message>` on standard output, exactly what earlier versions printed (tools
  and tests match on it; messages keep their Chinese / English text). The location, the source line and the call stack
  follow on standard error.
- **The location** is `file:line:column`, the column counted in characters (a Chinese character is one). The file is the
  module's path in the application (`main.aqua`, `lib/helper.aqua`, a library's module path).
- **What is underlined** is the part of the expression that failed: a missing member's name (`Length`), a whole operation
  for a type error (`x - 1`), a whole call for a wrong number of arguments or a call of something that is not a function,
  the indexing for an index error, the identifier for an undefined name, the `await` for a rejected promise, the
  `import(...)` for an import that fails. An expression spanning several lines is underlined to the end of its first
  line, followed by `... (continues to line N)`.
- **Alignment:** tabs count as 4 columns and East Asian wide characters (Chinese, Japanese, Korean, full-width forms) as
  two, so the `~~~` stays under the text in a terminal.
- **The call stack** lists the innermost function first: its name (the variable, field or hash key it was assigned to;
  `<anonymous>` otherwise, `<top level>` for a module's own code, `(built-in)` for the runtime's own functions such as
  `Array.Map`'s loop), then `file:line` of the call. A recursion repeating the same frame prints it once and says how
  many more times it repeats.
- **Imports:** an error inside an imported module shows the module's line, then the import in the importing module, as
  one call stack. Modules run again on every import; each run reports its own lines.
- **Colours:** red for `ERROR:` and the marks, blue for the gutter, only when the stream is a terminal that shows colours
  (Windows consoles get virtual terminal processing turned on). Never with `NO_COLOR` set to anything
  (https://no-color.org) or when the output is redirected to a file or a pipe. `AQUARIUS_COLOR=1` forces colours and
  `AQUARIUS_COLOR=0` turns them off.

Compile errors (syntax, type annotations) were already reported as `file:line:column: message`; that first line stays,
and the source line with the offending token underlined now follows it.

## Every kind of runtime error / 各種錯誤

All errors the runtime raises are located: undefined names, missing members (of modules and structs), type errors of
operators, annotations and conversions, calls with the wrong number of arguments or of non-functions, index and
assignment out of range, integer division and remainder by zero, failed imports (a missing script or an error inside
the imported one), errors returned by native library functions (argument checks and failures of a library's own), and
user-raised ones (an awaited `承諾.拒絕` / `Promise.Reject`). Errors raised inside fully typed
functions compiled to native code (the native tier) are located at their instruction too. The tests are
`compiler/AquariusTests/compiler/RuntimeErrorDiagnosticsTest.cs`.

A WebAssembly trap (a runtime bug, or memory that cannot grow) keeps its one-line message: a trap does not leave the
frames in a state that names the failing instruction.

## How it works, and what it costs / 原理與成本

Nothing is recorded while a program runs normally:

- The compiler keeps the source range of every instruction (the parser records each expression's extent;
  `SourceRanges`, `LoweredProgram.Spans`) and writes an `aquarius.debug` custom section
  (`compiler/AquariusCompiler/wasm/WasmDebugInfo.cs`): each module's path and source, and per compiled function its
  name, its block starts and the UTF-8 range of each instruction.
- Compiled code stores the failing instruction into its frame only on the path that leaves the function after an error
  (one store in code that already exits). Callers keep the continuation of the call they are in, which names the call.
  A native-tier function passes its function and instruction to `rt_nativeFailure`.
- When an execution fails, the host (`runtime/host/diagnostics.c`) reads that frame chain before releasing it, and
  reads the debug section only to print the error.

Measured with `examples/perf_micro/run-micro.ps1` (median of 7 runs, windows-x64), before and after: array_sum 10.0 /
9.7 ms, hash_string 16.9 / 17.0 ms, string_concat 985 / 983 ms; a second round 8.5 / 8.8, 17.6 / 18.5, 983 / 975 ms. The
differences are within the run-to-run noise. Applications grow by their sources plus a few bytes per instruction.

## Builds / 建置

`aqua run` and every `aqua build` keep the debug section, so built applications (`.exe`, Linux, Android, web, iOS and
macOS) report errors the same way; the sources of the application's modules are inside the application.
`aqua build ... --no-debug-info` leaves the section out (for a release that should not carry its sources): errors then
print only their first line, `ERROR: <message>`. The REPL compiles without it, since its source replays earlier inputs.

## Platforms / 平台

The compiler and the guest runtime are the same for every target. The host part is in `runtime/host`, shared by every
platform; each platform layer only says whether a stream shows colours (`platform_terminal_colors`): Windows consoles
and POSIX terminals can, the web (the browser console) and Android and iOS (the system log) print plain text. Only the
windows-x64 runtime was rebuilt into `dist/aqua` with this change; the other runtime packs must be rebuilt
(`scripts/build-runtime.ps1 -Targets ...`) before their applications print locations. Older runtimes running a new
application print the first line only.

## 繁中摘要

執行期錯誤現在會顯示檔案、行號與欄位（以字元計），印出出錯的那一行原始碼並以 `~~~` 標出出錯的部分，再列出呼叫堆疊（函式名稱與
`檔案:行`）。第一行維持原本的 `ERROR: 訊息`（輸出到標準輸出，既有工具與測試不受影響），其餘輸出到標準錯誤。Tab 以 4 欄、中文
等全形字以 2 欄計算，標記會對齊原始碼。輸出是支援顏色的終端機時才上色；設定 `NO_COLOR` 或輸出被重新導向時不上色，
`AQUARIUS_COLOR=1` 可強制上色。正常執行時不做任何記錄：編譯器在 `aquarius.debug` 區段存放原始碼與指令位置表，只有出錯的路徑
才會寫入出錯的指令。`aqua build --no-debug-info` 可省略這個區段（錯誤只印第一行）。
