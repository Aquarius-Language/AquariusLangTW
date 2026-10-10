# wgpu triangle

Run `dotnet run --project AquariusDesktop -- AquariusDesktop/examples/wgpu_triangle/main.aqua`
from the repository root. No window or GLFW build is needed. The script creates
a GPU render target, draws a WGSL triangle, checks a pixel against the background,
and saves `wgpu-triangle.png` beside the script. The odd dimensions also exercise
WebGPU's 256-byte row alignment during readback.
