# wgpu for Aquarius

The desktop VM provides `匯入("WGPU")` (also `匯入("wgpu")`) using
[Silk.NET 2.23.0](https://github.com/dotnet/Silk.NET) and its pinned
[wgpu-native runtime package](https://www.nuget.org/packages/Silk.NET.WebGPU.Native.WGPU/2.23.0).
This is a focused rendering and compute API, with managed resource ownership.
It supports GPU storage buffers, WGSL compute, offscreen rendering, and pixel
readback. It does not expose every WebGPU descriptor or arbitrary native pointers.
Processing uses the same backend for its P2D/P3D canvases and window presentation.
The `GL`, `GLAD`, `GLFW`, `GLM`, and `STBImage` modules remain available.

Importing either graphics module does not initialize the GPU. `CreateDevice()`
requests a native adapter and device. Missing adapters and shader/validation
errors become Aquarius runtime errors. `device.Backend` reports the selected API
(for example Vulkan, D3D12, or Metal). A suitable native GPU driver is required.
NuGet supplies wgpu-native binaries for Windows, Linux, and macOS; keep the
published runtime files with the application. Windows x64/Vulkan is verified;
other platforms require testing on their respective hosts.

## Compute

```text
變數 GPU = 匯入("WGPU");
變數 裝置 = GPU.CreateDevice();
變數 資料 = 裝置.CreateBuffer([1, 2, 3, 4]);
裝置.Dispatch("@group(0) @binding(0) var<storage, read_write> data: array<f32>;
@compute @workgroup_size(1) fn cs_main(@builtin(global_invocation_id) id: vec3<u32>) {
    if (id.x < arrayLength(&data)) { data[id.x] *= 2.0; }
}", 資料, 4);
印出(資料.Read());
裝置.Dispose();
```

| Object | Members |
| --- | --- |
| WGPU module | `Backend` (`wgpu-native`), `CreateDevice()` |
| Device | `Backend`, `CreateBuffer(floatArray)`, `Dispatch(wgsl,buffer,xWorkgroups)`, `CreateShader(vertexWGSL,fragmentWGSL)`, `CreateRenderTarget(width,height)`, `Poll()`, `Dispose()` |
| Buffer | `Length`, `Write(floatArray)`, `Read()`, `Dispose()` |
| Shader | `Set(name,floatArray)`, `SetInt(name,value)`, `Dispose()` |
| Render target | `Width`, `Height`, `Clear([r,g,b,a])`, `Draw(shader,vertexFloatArray)`, `ReadPixels()`, `Save(path)`, `Dispose()` |

Buffers contain float32 values, have a fixed length, and are limited to 64 MiB.
Compute shaders bind that buffer at group 0/binding 0 and use the `cs_main`
entry point. Dispatch accepts 1..65535 workgroups along X; Y/Z are 1. Shaders
must guard indexes when their workgroup size covers more elements than the buffer.
`Read()` waits for GPU completion and uses a separate mapped staging buffer.
Writing requires exactly `Length` values. Disposing a device releases its resources;
disposal is idempotent and subsequent use returns an error. Resources from
different devices cannot be mixed. Desktop runtime disposal also releases devices.

## Rendering

`CreateRenderTarget` creates an RGBA8 texture with a depth/stencil attachment.
Dimensions are 1..8192, with at most 16 million pixels. `Draw()` draws triangles;
each vertex has 12 float32 values: XYZ position, XYZ normal, UV, RGBA color.
Each triangle therefore needs 36 values. The low-level vertex shader supplies
WebGPU clip coordinates (X/Y -1..1, Z 0..1). RGBA clear/color values are in 0..1.
`ReadPixels()` returns packed, straight-alpha ARGB values in top-to-bottom order.
`Save()` writes PNG or PPM. Odd texture widths are supported through padded GPU
copies; padding is removed before returning pixels.

The shader interface is shared with Processing. A WGSL header is prepended to
each source, defining `VertexInput`, `VertexOutput`, `ProcessingUniforms`,
`processing`, `surface`, and `surfaceSampler`. Do not redeclare those names.
Use `vs_main(input: VertexInput) -> VertexOutput` and
`fs_main(input: VertexOutput) -> @location(0) vec4<f32>` entry points.
`VertexInput` has `position`, `normal`, `texcoord`, and `color`; `VertexOutput`
has `position`, `vertexColor`, `uv`, `normal`, and `eyePosition`.
Unused outputs can be zero initialized. The full header is in `WgpuShaders.cs`.
See [the triangle script](../examples/wgpu_triangle/main.aqua).

## Processing migration and shaders

`Processing.backend` is `wgpu`. Existing shape, color, transform, image, text,
pixel, lighting, PGraphics, and sketch callback APIs keep their names. GLFW
creates a `GLFW_NO_API` window and supplies input; wgpu owns its surface and
presentation. Processing no longer makes an OpenGL context current or calls
GLAD. Close a sketch before creating a raw OpenGL window in the same runtime.
Build the updated native bridge before running Processing.

Custom Processing shaders now use WGSL instead of GLSL 330. The default vertex
shader converts Processing's public -1..1 depth matrices to WebGPU's 0..1 depth.
Custom vertex shaders using those matrices must also apply
`clip.z = (clip.z + clip.w) * 0.5`. The shared uniform at group 0/binding 0
contains `model`, `view`, `projection`, `normalMatrix`, flags, materials, and lights.
Group 0/bindings 1 and 2 contain the image texture and sampler.

Declare custom uniforms in a `UserUniforms` struct at group 1/binding 0:

```wgsl
struct UserUniforms { time: f32, color: vec4<f32>, };
@group(1) @binding(0) var<uniform> user: UserUniforms;
@fragment fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    return vec4<f32>(user.color.rgb * (0.5 + 0.5*sin(user.time)), user.color.a);
}
```

`shader.set("time",value)` and `shader.set("color",[r,g,b,a])` write those fields.
Supported field types are `f32`, `i32`, `u32`, `vec2/3/4<f32>`, and
`mat4x4<f32>`, up to 1024 bytes. Use ordinary comma-separated fields without
layout annotations; host uploads follow WGSL alignment. Use `setInt` for integer
fields. Boolean uniforms and sampler reassignment are not supported. Return
straight RGBA for normal blending; custom translucent REPLACE output must be
premultiplied. The built-in shader handles this conversion itself.
The [showcase](../examples/processing_showcase/main.aqua) demonstrates a time uniform.

## Verification

Headless compute/render examples need only .NET and wgpu-native. Processing
examples additionally need the native GLFW bridge and a desktop:

```powershell
dotnet run --project AquariusDesktopVMREPL -- AquariusDesktopVMREPL/examples/wgpu_compute/main.aqua
dotnet run --project AquariusDesktopVMREPL -- AquariusDesktopVMREPL/examples/wgpu_triangle/main.aqua
./native/build.ps1
$env:AQUARIUS_GRAPHICS_FRAMES = '2'
dotnet run --project AquariusDesktopVMREPL -- AquariusDesktopVMREPL/examples/processing_wgpu/main.aqua
Remove-Item Env:AQUARIUS_GRAPHICS_FRAMES

dotnet test AquariusLangVMTesting -c Release -m:1
$env:AQUARIUS_WGPU_TESTS = '1'
$env:AQUARIUS_OPENGL_TESTS = '1'
dotnet test AquariusLangVMTesting -c Release -m:1 --filter FullyQualifiedName~AquariusREPL.Graphics
Remove-Item Env:AQUARIUS_WGPU_TESTS,Env:AQUARIUS_OPENGL_TESTS
```

Default tests cover imports, argument checks, uniform packing, clipping, example
parsing, and CLI compilation without GPU initialization. Opt-in tests verify
GPU compute/readback, WGSL triangles, contours, transparency, image orientation,
shader uniforms, smoothing changes, callback cleanup, raw OpenGL compatibility,
and source/bottle execution in separate CLI processes.
