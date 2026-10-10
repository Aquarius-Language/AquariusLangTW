# Processing on wgpu

Build the GLFW bridge with `./native/build.ps1`, then run:

```powershell
dotnet run --project AquariusDesktop -- AquariusDesktop/examples/processing_wgpu/main.aqua
```

This sketch composites a lit P3D cube into a P2D canvas. Space pauses and Esc exits.
The composition fits the current window, and the cube texture updates its pixel
resolution as the window or display density changes. Resizing also redraws while paused.
For an automated smoke run, set `AQUARIUS_GRAPHICS_FRAMES=2`; optionally set
`AQUARIUS_GRAPHICS_CAPTURE` to a PNG path. See the larger `processing_showcase`
example for custom WGSL uniforms, text, curves, particles, and images.
