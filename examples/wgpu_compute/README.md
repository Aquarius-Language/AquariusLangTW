# wgpu compute

Run `dotnet run --project AquariusDesktopVMREPL -- AquariusDesktopVMREPL/examples/wgpu_compute/main.aqua`
from the repository root. A wgpu adapter is required; no window or GLFW build is needed.
The script dispatches a WGSL compute shader, reads the GPU storage buffer, and
checks that `[1,2,3,4]` became `[2,4,6,8]`. It returns `真` on success.
