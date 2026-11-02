# 變更紀錄 / Changelog

One version covers the language, the compiler, the CLI, the language server and the VS Code extension; see
[versioning](docs/versioning.md).

## Unreleased

Language:

- The portable `數學` / `Math` module: `Abs`, `Sign`, `Min`, `Max`, `Clamp` (keeping the number type), `Floor`,
  `Ceil`, `Round` (halves away from zero), `Trunc` and their `*Int` variants, `Sqrt`, `Cbrt`, `Hypot`, `Pow`, `Exp`,
  `Log`, `Log2`, `Log10`, trigonometric and hyperbolic functions, `IsNaN`, `IsFinite`, `Fmod`, a seeded PCG32 generator
  (`Random`, `RandomInt`, `Seed`) and the constants `PI`, `E`, `TAU`, `Infinity`, `NaN`, with Chinese names
  (`平方根`, `正弦`, `次方`, `四捨五入`, `圓周率`, ...). Results follow IEEE 754 (no domain errors) and are bit-identical
  on every target: the transcendental functions are musl 1.2.5's libm, compiled into the guest runtime. Typed
  functions compile Math calls natively. Every application is about 39 KB larger. See [numbers](docs/math.md).
- Number literals: `1.5` and `1e-9` are doubles (unsuffixed decimals used to be a lexical error), `2.5e3f` and `2f`
  floats, `2d` a double, `0xFF` / `0b1010` integers (32-bit patterns), and `_` separates digits (`1_000_000`).
  `字串.解析數字` / `String.ParseNumber` reads number text exactly (an integer within 32 bits, else a double, or null).
- `%` on floats and doubles is the exact IEEE remainder (C `fmod`) in every tier: `1e17 % 3` is 1 (was 0), `-0.0 % 3`
  is -0 and `5 % Infinity` is 5 (was NaN).
- `字串.轉文字` / `String.From` prints numbers exactly like `印出` (`1E+17`, `1E-05`, `-0`, `1E+09` for floats) instead
  of JavaScript's style (`1e+21`, `1e-7`, `0`); both use one exact shortest-digit algorithm, and 印出's own output is
  unchanged.
- Integer bitwise operators `&`, `|`, `^`, `~`, `<<`, `>>`, `>>>` and `&= |= ^= <<= >>= >>>=`, with C#/JavaScript
  precedence and shift counts masked to 5 bits; non-integer operands are errors.
- 64-bit integers: `長整數` / `long` with `L` literals (`5000000000L`, `0xFFL`), `長整數(x)` / `long(x)`, `長整數[]`,
  `字串.解析長整數` / `String.ParseLong`. An int with a long gives a long, a long with a float or double a double; `+ - *`
  wrap like i64, comparisons are exact, bitwise operators mask shift counts to 6 bits, conversions saturate, longs print
  as plain decimals and `型別` gives `long`. `整數` / `int` stays 32-bit; an out-of-range int literal now suggests its
  `L` form. Generic compiled code computes proven longs inline as i64; the native tier does not take longs yet. The
  library SDK moves to API 3 (`AQUA_LONG`, `aqua_long`, `aqua_arg_long`, `aqua_return_long`); API 2 libraries still
  load.
- `arityOf` / `參數個數` gives the parameter count of a script or async function (-1 for native ones).
- Async functions and promises: `async function` / `非同步 函式` returns a promise and runs as its own task, `await` /
  `等待` waits for one (inside async functions and at a script's top level), and the `Promise` / `承諾` module adds `New`,
  `Resolve`, `Reject`, `All`, `AllSettled`, `Race`, `Any`, `Delay`, `Timeout`, `Then`, `Catch` and `Finally`. The words
  stay names wherever they start no async function or await, so existing programs keep working. Unhandled
  rejections are reported when the program ends. See [async functions and promises](docs/async.md).
- `&&` and `||` short-circuit: the right operand is skipped when the left one decides the result (`f != null &&
  f(x)`, `false && g()` no longer calls `g`). Programs whose right operands had no effects keep their output.

Libraries:

- Graphics: Processing's `sin`, `cos`, `tan`, `asin`, `acos`, `atan`, `atan2`, `exp`, `log`, `pow` and GLM's `Sin`,
  `Cos` run a vendored copy of musl's libm, so they equal `Math` bit for bit on every target (before, each target's C
  library computed them). Processing keeps its own conventions (`round` halves up, domain errors). `Models` uses
  `Math.Sqrt` instead of a Newton loop.
- Network: a Go-style server layer (`net/http`): `Handle("GET /items/{id}", handler)` patterns with optional methods,
  `{name...}` wildcards, the most specific route winning (literal over parameter over wildcard), `Group(prefix)` routers
  with their own middleware, wrapping middleware `function(request, writer, next)`, `ListenAndServe(":8080", routes)`,
  graceful `Shutdown(timeout)`, `StatusText`, `readTimeout` / `idleTimeout` options, writer `Error`, `NotFound`,
  `Write`, `AddHeader` and `File` (ETag, Last-Modified, byte ranges), request `Host`, `Proto`, `FormValue`, `Files`
  (multipart uploads) and `BasicAuth`. Listen accepts `host:port` and `:port`. Handlers and middleware may be async.
- Network: WebSocket subprotocols (`protocols` option, `UpgradeWebSocket([protocols])`, `Protocol` field) on native
  clients, servers and the browser; `RandomBytes(n)` (secure randomness) and `PageOrigin()` (the web page's origin).
- Network: a WebSocket Receive waits as long as the next message takes (the timeout option limits connecting and
  sending); server WebSockets no longer end when the upgrade request deadline passes.
- Network: every waiting function has a promise variant, `...Async` / `非同步...` (`GetAsync`, `ServeAsync`,
  `AcceptAsync`, `ReceiveAsync`, ...), on every target that has the function.
- New default library `interaction`: `Interaction` / `互動` turns window events into each platform's clicks (with
  click counts), drags with release velocity, long presses, pinches, context menu requests, back and forward,
  scrolling, zooming and keyboard commands, reports the platform's conventions (double-click time, touch slop,
  scrollbar style, dark mode, reduced motion, ...) and works with any graphics library (`Attach` a shared window or
  `Feed` raw events). `ScrollView`, `Navigation`, `ContextMenu` and `Focus` are written in AquariusLang on top of it.
- New default library `webframe`: `WebFrame` / `網頁框架` embeds other pages (iframes) in a web application's page,
  placed in the application's coordinates (web glue domain `frame`); other targets report it unsupported.
- Interaction: `text` and `composition` events (typed text and input-method composition) with session
  `TextInput(enabled)` and `TextRect(x, y, width, height)`, for text editors drawn by any graphics library.
  On the web a click keeps typing in the text input, and a window inside a frame no longer takes focus from the page
  around it when it opens. A full-screen canvas on a high-density display starts at the viewport's size instead of
  the screen's device pixels times the density again (which exceeded the GPU's texture limit).
- Interaction: `CursorArea(name, x, y, width, height, cursor)` / `RemoveCursorArea(name)` show a cursor shape (the
  text cursor over editable text) while the pointer hovers a rectangle and the arrow elsewhere; `composition` events
  carry the caret in the composed text (`cursor`), and `Feed` takes a composition's `phase`.
- SDK: `aqua_window_text_input_enabled(window)`. Processing's Escape no longer closes a sketch while another library
  (an editor) has text input on, nor on the web, where a page has no window to close; `run` ends without a value,
  so a script ending with it no longer prints `null`.
- Keypad keys with Num Lock off arrive as the navigation keys printed under them (Home, End, Page Up/Down, arrows, Insert,
  Delete) on Windows, Linux, Android and the web, for every library; the scancode stays the physical key. Android maps
  Home, End, Page Up/Down, Insert, the keypad, brackets, lock keys and Meta, and reports Meta, Caps Lock and Num Lock
  in `mods`. Processing adds `INSERT`, `HOME`, `END`, `PAGE_UP` and `PAGE_DOWN`. See [the input roadmap](docs/input-roadmap.md).
- Processing: text whose font renders wider at the screen's scale than at its measured size (細明體, which a `zh-Hant`
  page uses for `monospace`, rounds advances to whole pixels) is fitted to the box `textWidth()` reports, so full-width
  and Chinese text no longer runs into what follows it on high-density displays and editor carets stay on the text.
- Resizing and resolution changes: windows send `resize` when only their density changes too (web every poll, desktop
  on content-scale changes); Processing's `clip`, boxed `text` and filled shapes take empty and negative sizes (an empty
  fill draws nothing instead of failing with "Polygon has zero area."); `ScrollView.Place` clamps negative sizes.
- Exported web pages hide the empty output and error boxes, which covered the top of full-window canvases and took
  the pointer there.
- Processing's `platformWindow` and WGPU windows' `PlatformWindow` give other libraries the shared window object.
- Web glue domains `history` (the browser's back button) and `mediaquery` (`matchMedia`).
- ONNX: models up to 2 GiB (protobuf's limit) instead of 256 MiB, so YOLOX-X (396 MB) and other large models load.
- ONNX: models saved with external data (a small `.onnx` plus separate weight files, how models over 2 GiB are stored)
  open from files (weights read from the model's folder) and from the package (packaged weight files handed to ONNX
  Runtime from memory, on every target including the web).

SDK (API version 2): `aqua_define_async` registers a promise variant of a function; `aqua_object_member` gives a
method of a native object as a function value; `aqua_invoke_async` waits for a promise a callback returns. A deferred
call that settled before its function returned no longer leaves its caller waiting. The bulk array transfers
(`aqua_array_read_*`, `aqua_array_write_*`, `aqua_array_from_*`) ask the engine for guest memory once per call instead
of once per element: 2–3 times faster on million-element arrays (an image's bytes, a model's input tensor).

Docs: [native-like applications](docs/native-like-apps.md), the rules and checklist for drawn interfaces on every
platform.

## 0.1.6

Programs keep their meaning and output.

Runtime:

- Guest member reads on library classes (`p.frameCount`, `p.mouseX`) look the name up in a hash built once per class,
  instead of converting it and scanning every alias (about 7 times faster).

Processing library:

- Faster drawing: consecutive draws with the same state merge into one GPU draw, transforms run on the CPU, ellipses
  and arcs pick their segment count from their on-screen size, and `set()`, `loadPixels()`, `updatePixels()` and
  `BLUR` avoid needless readbacks and copies.
- More of Processing's API: the remaining blend modes, `blend()` / `blendColor()`, `DILATE` / `ERODE`, `copy()`,
  `background(image)`, `textureWrap()`, `frustum()`, `beginCamera()` / `endCamera()`, `hint()`, `applyMatrix()`,
  `rotate(angle, axis)`, primitive and group `PShape`s, `shapeMode()`, OBJ models in `loadShape()`, more `PVector`
  methods, date and time, the `nf*` / `hex` / `binary` / `str` / `trim` / `split` / `join` helpers, cursors,
  `windowTitle()`, `pixelDensity()`, `focused` and the key constants.
- `curvePoint()` / `curveTangent()` follow `curveTightness()`; `bezierDetail()` and `curveDetail()` no longer change
  ellipses.

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
