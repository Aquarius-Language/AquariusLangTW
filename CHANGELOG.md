# 變更紀錄 / Changelog

One version covers the language, the compiler, the CLI, the language server and the VS Code extension; see
[versioning](docs/versioning.md).

## Unreleased

Language:

- Declarative syntax for widget trees and components: trailing commas in arrays, calls, parameter lists and struct
  fields; struct field defaults (`struct Style { size: double = 14.0d; tags = [] }`, array and hash defaults copied
  per struct); positional arguments before named ones (`Text("Hi", weight: 700)`); parameter defaults and named
  arguments for functions (`var badge = function(label, tone = "accent") { ... }; badge("New", tone: "warn")`);
  `if`, `elif`/`else`, `for` and `...spread` elements in array literals (`[header, if (signedIn) profile else login,
  for (var i = 0; i < n; i++) row(i), ...footer]`); short functions `x => x * 2`, `(a, b = 1) => a + b` and
  `() => { ... }`. Callbacks from libraries may leave out parameters that have defaults.

- Constants: `常數 名稱 = 值;` / `const name = value;` declare a binding assigned once; assigning it again (`=`, `+=`,
  `++`) or declaring the name again in the same scope is a compile error. `常數` and `const` are keywords only where a
  declaration starts, so programs using them as names keep working. Typed functions and loops written after a constant
  initialized with a number or Boolean literal use the value directly. The language server hovers, completes and
  lists constants, and the VS Code grammar highlights them.
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

Tools:
- `aqua_wellplay` (new, `tools/aqua_wellplay`, its own repository as a submodule): AquariusLang notebooks in the spirit
  of JupyterLab. `aqua_wellplay [folder]` serves a folder over HTTP; the browser frontend (a SaturnUI app built for the
  web) edits `.ipynb` notebooks with Aqua_Embedded_LSP_Editor cells, and cells run on the desktop through the kernel,
  which streams text, errors and rich outputs (`Display`: Markdown, tables, charts, images, JSON, progress, updatable
  outputs) and shows a cell's last value as `Out[n]`. Light and dark themes, a file browser, Jupyter's shortcuts,
  autosave, interrupt and restart. The release builds `aqua_wellplay(.exe)` beside `aqua` (`scripts/publish-cli`
  builds every tool under `tools/`). See [tools/aqua_wellplay](tools/aqua_wellplay/README.md).

Compiler and CLI:
- Compile errors carry their line and column: `main.aqua:2:14: message` (`CompilationException.Diagnostics`).
- `aqua run` takes a script inside a project folder that the project does not declare, running it with the project's
  libraries (it used to fail with `Source is not declared in project.sources.`; builds still require it).

Libraries:
- `terminal`: `Terminal.Start(executable, arguments, options)` returns a `Terminal.Process` whose output reaches
  `OnOutput` / `OnError` callbacks as it arrives (in order; a callback may be async and wait), with `Wait`,
  `WaitAsync`, `Kill` (the whole tree), `Running` and `Result`.
- `application`: `Application.Arguments()` / `啟動引數` returns the launch arguments as strings (command-line options).
- `graphics`: `loadImage(bytes)` decodes an encoded image held in memory.
- `saturn`: `Immediate` widgets take `ime()` (the input method follows their caret) and `focus(focused)`, get every
  Interaction field (modifiers, input-method phases) and let unused wheel turns scroll the view around them; new icons
  `play`, `fastForward`, `stop`, `refresh`, `save`, `trash`, `file`, `folder`, `notebook`, `sun`, `moon`, `code`,
  `text`, `copy`, `more`, `edit`.
- `saturn` (new): Saturn / 土星 lays out interfaces with Meta's Yoga (pinned upstream, compiled in) under Yoga's own
  API (`Saturn.Node`, `Saturn.Config`, every style property, measure/baseline/dirtied callbacks), CSS-named styles
  (`SetStyle`, `ReplaceStyle`) with CSS text inheritance and transitions, text with word and CJK line breaking, images,
  painting into the render-agnostic UI draw list, hit testing, `Saturn.Painter` and `Saturn.Combine`. Its `SaturnUI` /
  `土星介面` module adds Flutter-style widgets (layout, text, images, icons, buttons, checkboxes, switches, radios,
  sliders, text fields, dropdowns, tabs, dialogs, tooltips, scrolling and virtualized lists), themes and style sheets
  with state and breakpoint selectors, events through Interaction, surfaces that draw anywhere, `UI.Layers` that mix
  SaturnUI, ImGui and Clay with one input router, and `UI.RunApp`. See [saturn](libraries/saturn/README.md).
- `imgui` (new): Dear ImGui 1.93.0 (docking branch) for any renderer: contexts fed by Interaction events, fonts with
  system CJK fonts merged, the widget API under Dear ImGui's names (editors return the new value), draw lists, docking,
  drag and drop, styles, and `Embed` items other UI libraries draw into. See [imgui](libraries/imgui/README.md).
- `clay` (new): Clay 0.14 layouts declared each frame (`Clay.Element`, `Clay.Text`, sizing, floating, scrolling,
  borders, images, embedded draw lists). See [clay](libraries/clay/README.md).
- `graphics`: `drawUI(list)` draws UI draw lists natively on sketches and canvases (rounded rectangles, borders,
  shadows, gradients, text, images, textured meshes and their textures, clips, transforms, opacity);
  `uiTextMetrics()` measures text as `drawUI` draws it; font names take `:<weight>` and `:italic` on every platform.
- Fix: `module.Struct(b: 1, a: 2)` filled the struct's fields in the order written; named arguments through a module
  member now match fields by name.

- Breaking: a library's `[[assets]]` `target` is now relative to its own `/bottles/NAME/` folder, like an asset with no
  `target`, so a library can no longer place files at the application root and two libraries (or a library and the
  application) declaring the same target no longer clash. A library that used a root-level target, such as
  `target = "native/plugin.dll"`, now finds it at `/bottles/NAME/native/plugin.dll`. The application's own targets are
  unchanged. A conflicting path error now names both declarations.
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

Performance (typed code, desktop, large benchmark inputs; see `reports/csharp-parity-2026-11.md`):

- Self tail calls in typed functions are loops (Ackermann −25%); entry checkpoints use two branches instead of
  combined flags (Fibonacci −7%, N-Queens −9%); inlined leaves run no entry checkpoint of their own (spectral norm
  −16%); only loops that allocate poll for collections (matrix −26%, sieve −25%).
- Versioned loops: an innermost counted loop runs a copy without its per-iteration fuel check when the budget covers
  it, and without bounds checks on indices proven in range before the loop (matrix −28% together, spectral norm −11%,
  N-Queens −9%). Results, errors, budgets and yield points are unchanged.
- Arrays of up to four elements are one heap block (binary trees −11% to −17%).
- A long typed recursion (functions without loops) yields to other tasks every run slice again: it never yielded once
  its native budget went below zero, which could freeze the UI on web and Android. Costs Ackermann about 10% and typed
  binary trees 20–40% on desktop (collections now run during the recursion).
- `aqua build --target windows|linux --optimize true` compiles the application ahead of time: the pinned wasm2c
  translates the same `.wasm` to C and the pinned Clang compiles it into `app.aot.dll` / `app.aot.so` beside the
  executable, which the runtime pack's new `aquarius-aot` launcher runs (20–30% less time than Wasmtime across the
  benchmarks). The toolchain installs on first use (`scripts/toolchain.json` pins WABT). Chosen over WAMR by a
  bake-off; see the report.

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
