# Open performance items

Current, still-applicable findings carried over from the
[2026-10-09 performance review](archive/performance-review.md), which otherwise describes the
retired VM. Ranks refer to that review. Update this list when an item lands.

| Rank | Item | Where | Status |
| ---: | --- | --- | --- |
| 5 | Reuse GPU vertex/uniform storage and bind groups instead of creating buffers and bind groups per Processing draw | `AquariusDesktop/graphics/WgpuDevice.cs` (`WgpuTarget.Draw`), `AquariusWebCompiler/browser/wgpu.mjs` | Open |
| 7 | Remove per-vertex and per-matrix packing allocations | `AquariusDesktop/graphics/ProcessingCanvas.cs` (`Draw`), `AquariusDesktop/graphics/WgpuShaders.cs` (`Uniforms`) | Open |
| 14 | Cache immutable geometry and triangulation (unit spheres by detail, retained shapes) | `AquariusDesktop/graphics/ProcessingDrawing.cs`, `ProcessingShapes.cs` | Open |
| 15 | Cache unchanged normal matrices and uniform components while model/view are unchanged | `AquariusDesktop/graphics/WgpuShaders.cs` (`Uniforms`) | Open |

Each change must keep draw order, per-draw uniform snapshots, clipping/scissor, stencil,
MSAA, offscreen targets, resize and disposal behavior, and be measured on a many-draw
Processing scene before and after.
