# Aquarius desktop graphics

The desktop interpreter supports a complete **OpenGL 3.3 core** function surface:
344 entry points and the constants from the vendored GLAD header. This includes
VAO/VBO/EBOs, GLSL, uniforms, textures, samplers, depth/stencil/blending, FBOs,
renderbuffers, queries, sync objects, transform feedback and instanced drawing.
OpenGL 4.x and extension entry points are not generated in this version.

The portable language library (`AquariusLangInterpreted`) has no native graphics
dependency. Modules are registered only by the desktop host; importing them or
using math/buffer helpers does not initialize GLFW or load the graphics DLL.

| Aquarius import | Implementation |
| --- | --- |
| `GLFW` | GLFW 3.4 windows, OpenGL contexts, keyboard/mouse polling and time |
| `GLAD` | GLAD 2.0.8 generated loader, OpenGL 3.3 core, no extensions |
| `GL` | Every generated core function, constants, typed data and shader helpers |
| `GLM` | GLM-style math implemented with .NET System.Numerics; no C++ GLM dependency |
| `STBImage` | stb_image 2.30; PNG/JPEG/BMP/TGA/PNM and other supported formats |
| `Processing` | Processing-style 2D/3D drawing, sketch loop/input, images/pixels/text, offscreen canvases, retained shapes, lights/materials and GLSL shaders |

See the [Processing API guide](../AquariusDesktopInterpretedREPL/graphics/Processing.md)
and [interactive showcase](../AquariusDesktopInterpretedREPL/examples/processing_showcase/README.md).
Rebuild the native library when updating to this module: its event loop uses
new bridge exports for window size, character input, wheel input and fullscreen.

## Build

Use .NET 8 SDK (the existing desktop host targets .NET 6), CMake 3.20 or newer,
a C compiler, and an OpenGL 3.3-capable driver. GLFW is fetched from its 3.4
release with a verified SHA-256 checksum. GLAD and stb_image are vendored, with
licenses, so users need no Python or GLAD generation step for ordinary builds.
The first native configure needs network access; later builds use CMake's cache.

From the repository root on Windows with Visual Studio C++ tools installed:

```powershell
./native/build.ps1
dotnet build AquariusLang.sln
dotnet run --project AquariusDesktopInterpretedREPL -- AquariusDesktopInterpretedREPL/examples/opengl_cube/main.aqua
```

`build.ps1` accepts `-CMake <path>`, `-Generator <name>` and `-Runtime <rid>`.
For an existing build it reuses the cached generator and CMake executable, even
when the shell's default generator differs. Explicitly selecting another
generator creates a separate directory under `native/build/variants`, preserving
the existing build. An explicit `-CMake` always takes precedence.
If the CMake on PATH predates your Visual Studio installation, pass the CMake
bundled with Visual Studio. The runtime architecture must match the C compiler
architecture, e.g. `win-x64` or `win-arm64`.

On Linux/macOS, install your platform's GLFW build prerequisites (Linux X11
and/or Wayland development libraries; macOS Xcode command-line tools), then:

```sh
cmake -S native -B native/build -DCMAKE_BUILD_TYPE=Release
cmake --build native/build --parallel
cmake --install native/build --prefix AquariusDesktopInterpretedREPL/runtimes/linux-x64/native
dotnet build AquariusLang.sln
```

Use the matching RID in the install path: `linux-x64`, `linux-arm64`, `osx-x64`,
`osx-arm64`, etc. The project copies installed libraries to build and publish
outputs. Build the native library **before** building/publishing the desktop
host. Generated binaries/build directories are ignored by Git. Windows x64 has
been built and exercised; Linux/macOS builds are supported by the source but
have not been exercised here.

## Windows Application Control blocks the interpreter (0x800711C7)

If Windows reports `An Application Control policy has blocked this file` for
`AquariusDesktopInterpretedREPL.dll`, check the Windows Code Integrity event log.
Event 3077 with policy `VerifiedAndReputableDesktop` identifies Smart App Control.
This happens before Aquarius or OpenGL initializes: newly compiled, unsigned
binaries may have insufficient reputation to run under this policy.

To retain Smart App Control, sign the application and its executable/DLL
dependencies using an RSA code-signing certificate from a trusted provider or
Microsoft's signing service. A self-signed development certificate or a .NET
strong-name signature does not satisfy Smart App Control's trusted-provider
requirement. Rebuilt binaries must be signed again.
See [Microsoft's signing guide](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/code-signing-for-smart-app-control).

For a personal development PC, users may instead choose to turn Smart App
Control off in **Windows Security → App & browser control → Smart App Control
settings → Off**. This changes protection for the whole PC, not just Aquarius.
Read the Windows confirmation for your installed version before changing it.
On managed PCs, ask the administrator to provide an approved development policy.
Smart App Control does not offer individual-app exceptions; see
[Microsoft's FAQ](https://support.microsoft.com/en-us/windows/security/threat-malware-protection/smart-app-control-frequently-asked-questions).
The build scripts do not change Windows security settings.

## Start a graphics session

```text
變數 視窗庫 = 匯入("GLFW");
變數 載入器 = 匯入("GLAD");
變數 繪圖 = 匯入("GL");
視窗庫.Init();
變數 視窗 = 視窗庫.CreateWindow(800, 600, "Aquarius OpenGL");
視窗庫.MakeContextCurrent(視窗);
載入器.Load();
繪圖.glEnable(繪圖.GL_DEPTH_TEST);
# Your rendering loop here.
視窗庫.DestroyWindow(視窗);
視窗庫.Terminate();
```

Initialization requests OpenGL 3.3 core and forward compatibility. Override
hints after `Init` and before `CreateWindow`. `GLAD.Load()` returns GLAD's encoded
driver version (major * 10000 + minor), or an Aquarius error if 3.3 is unavailable.
The runtime supports one live window/context at a time and one GLFW-owning
interpreter per process. Use all graphics calls on the owning execution thread;
on macOS, GLFW requires the process main thread. Multiple contexts, monitor/
joystick APIs and callback registration are not part of the GLFW wrapper.

`DesktopBuiltins` is disposable. The command-line interpreter automatically
disposes it, releasing buffers and destroying the context even when evaluation
returns an error. Embedding hosts must likewise dispose `DesktopBuiltins`.
Explicit GL object deletion is recommended; context destruction also releases
remaining driver resources.

## Arguments and memory

Entry points keep their exact C names and argument order, e.g. `glBufferData`
and `glUniformMatrix4fv`. Numeric arguments are range checked. `GLboolean`
accepts `真`/`假` or 0/1. OpenGL Boolean returns are Aquarius Booleans.
Constants and unsigned results use exactly representable Aquarius doubles;
64-bit integer results and `GL_TIMEOUT_IGNORED` use opaque exact integer objects
which can be passed back to 64-bit GL arguments. They are not arithmetic values.

For typed pointer arguments, pass an Aquarius array. The binding copies inputs
to temporary native storage, invokes GL, and copies non-const outputs back into
the same array. Allocate enough elements for the C API's output contract:

```text
變數 緩衝 = [0];
繪圖.glGenBuffers(1, 緩衝);
變數 視口 = [0, 0, 0, 0];
繪圖.glGetIntegerv(繪圖.GL_VIEWPORT, 視口);
```

`const GLchar*` accepts a string; `const GLchar* const*` accepts an array of
strings (`glShaderSource`). Pointer-to-pointer arguments accept arrays of opaque
pointers, or element-buffer offsets for multi-draw operations.

For `void*` data, use `GL.FloatData(array)`, `GL.UIntData(array)`,
`GL.ByteData(array)`, or a zero-initialized `GL.Allocate(byteCount)` buffer.
`GL.ByteLength`, `GL.ReadBytes` and `GL.FreeData` manage these buffers. Maximum
buffer size is 256 MiB. Upload example:

```text
變數 資料 = 繪圖.FloatData([0.0f, 1.0f, 0.0f]);
繪圖.glBufferData(繪圖.GL_ARRAY_BUFFER, 繪圖.ByteLength(資料), 資料, 繪圖.GL_STATIC_DRAW);
繪圖.FreeData(資料);
```

The integer 0 means a null pointer. Nonzero integers are accepted as byte
offsets only for vertex attributes and indexed draws. Host pointers and mapped
buffers are opaque objects; they must not be reused after the originating GL
object is deleted/unmapped or its context is destroyed. Temporary array storage
is valid only during a GL call; APIs retaining host memory must receive a live
`GL.Allocate`/typed buffer instead. Common count/size mismatches are rejected,
but raw GL callers remain responsible for pointer capacity, pixel formats,
pack/unpack alignment and object/context validity, as in the native GL API.
Pixel transfer functions accept zero only when permitted by OpenGL (e.g. texture
allocation or a bound pixel buffer).

## Convenience functions

`GL.CreateProgram(vertexSource, fragmentSource)` compiles/links shaders and
reports complete driver logs as Aquarius errors. `GL.ShaderLog(shader)` and
`GL.ProgramLog(program)` also work with manually created objects.
`GL.ReadText(path)` loads shader files; `GL.ParseInteger(string)` parses decimal
configuration. `GL.SavePPM(path, width, height)` captures the current framebuffer
to a top-down binary RGB PPM. Capture the default back buffer before swapping.

`STBImage.Load(path, flipVertically)` returns `[width, height, 4, rgbaBuffer]`.
Upload the buffer with `glTexImage2D`, then release it with `GL.FreeData`.
For color textures, use `GL_SRGB8_ALPHA8`; use a linear internal format for
normal/roughness/data textures. stb_image file paths use UTF-8, including on
Windows.

`GLM` provides `PI`, `Radians`, `Sin`, `Cos`, `Sqrt`, `Normalize`, `Dot`, `Cross`,
`Identity`, `Translate`, `Scale`, `Rotate(angleRadians, axis)`,
`Multiply(a, b)`, `LookAt(eye, target, up)`, `Perspective(fovRadians, aspect, near, far)`,
`Transpose`, `Inverse` and `TransformPoint(matrix, point)`. Vectors are three
numbers; matrices are sixteen numbers in OpenGL column-major order. Upload
with transpose=`假`. `Multiply(a,b)` means `a * b` for GLSL column vectors;
`Perspective` uses OpenGL's [-1,1] depth range and `LookAt` is right handed.

## Test and regenerate

```powershell
dotnet test AquariusLang.sln
$env:AQUARIUS_OPENGL_TESTS = '1'
dotnet test AquariusDesktopInterpretedREPL
```

Native GPU integration is opt-in so normal tests work without a display/driver
or installed graphics binary. It runs the Aquarius cube, checks a framebuffer
capture and zero GL errors, then reinitializes GLFW to check resource cleanup.
The existing `TestExecuteFile` additionally requires a `python` executable
visible on PATH.

To regenerate the C function table and all C# delegates/constants after updating
the vendored GLAD header, run `python native/generate_bindings.py` (standard
library only). To regenerate GLAD itself, install `glad2==2.0.8` and run:

```sh
python -m glad --api gl:core=3.3 --extensions= --reproducible --out-path native/vendor/glad c
python native/generate_bindings.py
```

stb_image and its license come from commit
`f0569113c93ad095470c54bf34a17b36646bbbb5` of
[nothings/stb](https://github.com/nothings/stb).
GLAD includes its upstream license and Khronos header notices. GLFW carries
the zlib/libpng license in its fetched release. See
[GLFW](https://www.glfw.org/), [GLAD](https://github.com/Dav1dde/glad) and
[OpenGL reference](https://registry.khronos.org/OpenGL-Refpages/gl4/).
