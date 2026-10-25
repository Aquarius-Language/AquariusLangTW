# opengl — desktop OpenGL 3.3 core

Scripts drive OpenGL directly, as in C: open a window, load the entry points, upload geometry, compile GLSL and draw.

```aqua
var glfw = import("GLFW");   var glad = import("GLAD");   var gl = import("GL");
glfw.Init();
var window = glfw.CreateWindow(960, 720, "OpenGL");
glfw.MakeContextCurrent(window);
glad.Load();
var data = gl.FloatData([-0.5d, -0.5d, 0.5d, -0.5d, 0, 0.5d]);
...
for (var frame = 0; !glfw.WindowShouldClose(window); frame += 1) {
    glfw.PollEvents(); gl.glClear(gl.GL_COLOR_BUFFER_BIT); ... glfw.SwapBuffers(window);
}
```

`examples/opengl_cube` is a complete program (it also uses GLM from the graphics library).

## Modules

| Module | Contents |
| --- | --- |
| `GLFW` | `Init`, `Terminate`, `WindowHint`, `CreateWindow`, `DestroyWindow`, `MakeContextCurrent`, `SwapBuffers`, `SwapInterval`, `PollEvents`, `WindowShouldClose`, `SetWindowShouldClose`, `GetTime`, `GetKey`, `GetMouseButton`, `GetCursorPos`, `SetInputMode`, `GetFramebufferSize`, `GetWindowSize`, `SetWindowSize`, `GetEnvironment`, and the `GLFW_*` constants scripts used before |
| `GLAD` | `Load()`: loads every entry point for the current context and returns the version (`major * 10000 + minor`); fails below 3.3 |
| `GL` | all 344 OpenGL 3.3 core functions, the 829 integer constants (as doubles) and `GL_TIMEOUT_IGNORED`, plus `FloatData`, `UIntData`, `ByteData`, `Allocate`, `ByteLength`, `ReadBytes`, `FreeData`, `CreateProgram`, `ShaderLog`, `ProgramLog`, `ReadText`, `ParseInteger`, `SavePPM` |
| `STBImage` | `Load(path, flip)` → `[width, height, 4, GL_DATA of RGBA8 rows]` (PNG, JPEG, BMP, TGA, PSD, GIF, HDR, PIC, PNM) |

Every function also has its Traditional Chinese name (`gl.gl清除`, `glfw.建立視窗`, `gl.浮點資料`, ...), with the same
names and argument counts as the earlier implementation.

### Arguments of GL functions

Numbers become the parameter's C type: integral and in range for integer types (`GLboolean` also takes true/false, 64-bit
types also take `GL_INTEGER64`). Pointer parameters take:

- typed data (`GL_DATA` from `FloatData`, `UIntData`, `ByteData`, `Allocate`, `STBImage.Load`),
- driver pointers (`GL_POINTER`, from `glMapBuffer`, `glFenceSync`, ...), `0` or `null`,
- an array of the pointed-to type, copied back after the call when the driver writes into it (`glGenBuffers(1, ids)`,
  `glGetIntegerv(GL_VIEWPORT, [0, 0, 0, 0])`); 64-bit results become `GL_INTEGER64` objects (`.value`, `.text`), every
  other result a double,
- a string for `GLchar` text, an array of strings for `glShaderSource`-style lists,
- a nonnegative byte offset into the bound buffer for vertex attribute and index pointers (`glVertexAttribPointer`,
  `glDrawElements*`, `glMultiDrawElements*`).

Counts are checked against the arrays and buffers they describe (`glGenBuffers(4, [0])`, `glUniformMatrix4fv(loc, 2, ...)`
with fewer than 32 floats, `glGetShaderInfoLog` with a shorter buffer than `bufSize`, ...). Functions return doubles
for numbers, booleans for `GLboolean`, strings for `glGetString`, and null for `void`, as before. GL functions need
`GLFW.Init()`, a current window and `GLAD.Load()`; a failing call stops the script with `<function>: <message>`.

## How it is built

- `src/gl_functions.def` is the whole OpenGL surface, one line per function: its result type, name, Chinese alias, C
  parameter types and argument kinds (`n` number, `p` pointer, `o` pointer or offset, `c` count, `p16` = count × 16
  elements). `gl.c` expands it into the entry-point table (`opengl.h`), the GLAD-style loader, one typed wrapper per
  function and their registration; the C types are classified at compile time (`_Generic`), so a wrapper calls the
  driver with exactly its signature. `src/gl_constants.def` lists the constants the same way. Both tables were taken
  from GLAD 2.0.8's `gl.h` (`--api gl:core=3.3`, the Khronos registry), and are kept by hand: no generator.
- `src/glfw.c` uses only `<aqua/window.h>` (OpenGL windows, hints, context, swap, input, time, polling); it never
  calls GLFW. Windows are the runtime's shared window objects (`aqua_window_object`), so other libraries accept them.
- `src/image.c` compiles stb_image with every symbol static; `src/files.c` reads packaged assets or files (UTF-8 paths).

## Platforms

`windows` and `linux` (desktop OpenGL 3.3 core; WSLg provides it through Mesa). `CMakeLists.txt` declares
`PLATFORMS windows linux`, so Android and web builds with every library skip it. The library builds into the runtime
or on its own as `aqua_opengl.dll` / `libaqua_opengl.so` (`aquarius --library aqua_opengl.dll app.wasm`).

## Dependencies

| Dependency | Version | License | How |
| --- | --- | --- | --- |
| stb_image (`third_party/stb`) | 2.30 | MIT or public domain (`third_party/stb/LICENSE`) | vendored from the repository's earlier `native/vendor/stb`, SHA-256 `594c2fe35d49488b4382dbfaec8f98366defca819d916ac95becf3e75f4200b3` |
| OpenGL function and constant tables | 3.3 core | facts of the Khronos API (registry: Apache-2.0) | hand-kept `.def` tables, no GLAD code |

## Differences from the earlier implementation

- Windows are the shared `window` objects (`typeOf` → `window`, with `isOpen`) instead of `GLFW_WINDOW` objects. Input,
  size and close queries accept any live window object; `DestroyWindow`, `MakeContextCurrent` and `SwapBuffers` only
  the module's own window. One OpenGL window at a time and one owning runtime (`GLFW.Init`), as before.
- `print` shows native objects by type (`GL_DATA`, `GL_POINTER`, `GL_INTEGER64`) instead of "graphics buffer (n
  bytes)", "OpenGL pointer" or the 64-bit number: a runtime rule for native objects. `GL_INTEGER64` therefore gained
  `value` (a double) and `text` (exact digits).
- Wrong argument counts are reported by the runtime (`GL.glClear expects 1 argument, got 2.`); out-of-range integers say
  `-1 is out of range for its OpenGL type.` instead of .NET's overflow message; an unreadable file says
  `Cannot open <path>: <reason>` instead of stb's `can't fopen`.
- `null` is accepted wherever a null pointer (`0`) is.
- Safety only, never accepting less valid input: more count rules (info logs, compressed images, multi-draw, uniform
  queries, attached shaders, sync queries, draw buffers); byte offsets only for the attribute and index pointer
  parameters (before: any pointer parameter of those functions); arrays the driver writes into have room for at least 16
  elements (only the array's own elements are copied back); `GL_DATA` keeps a zeroed 64-byte tail.
- `WindowHint`: `GLFW_VISIBLE`, `GLFW_RESIZABLE`, `GLFW_DECORATED` and `GLFW_CLIENT_API` become window flags; the
  window system always creates a 3.3 core forward-compatible context. Other hints (`GLFW_SAMPLES`, ...) pass through.
- `SetInputMode`: `GLFW_CURSOR` sets the cursor mode (disabled also enables raw motion); sticky mouse buttons and
  lock-key modifiers are always on, other modes have no effect.

## Tests

`tests/<case>.aqua` with the exact output in `tests/<case>.expected`: `data` (no window), `render` (a hidden window's
framebuffer object, glReadPixels checksum, SavePPM/STBImage round trip), `arguments` (rules and a failure with the
window open), `cube` (the opengl_cube example with its GLM calls in `tests/support/glm.aqua`; compile with
`tests/support/glm.aqua --root tests --assets tests/support/checker.ppm`; 3 frames unless `AQUARIUS_GRAPHICS_FRAMES`
is set). The compiler reads the nearest `aqua.toml`, so compile copies of the tests outside this folder.
