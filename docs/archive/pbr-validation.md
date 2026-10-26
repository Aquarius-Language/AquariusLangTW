# PBR / model import validation — 2026-10-12

> Historical record. The repository has since replaced Zig with a pinned LLVM/Clang toolchain
> (`scripts/install-wasm-toolchain.ps1`); the results below were produced with Zig.

Validation ran on Windows x64 with the pinned Zig 0.15.1 compiler, .NET 8,
Wasmtime and actual wgpu-native / Chrome WebGPU rendering.

| Check | Result |
|---|---|
| Complete .NET suite, including WebGPU, OpenGL and native application opt-ins | 1,316 passed, no skips |
| Browser host / Wasm / library unit tests (`npm test`) | 141 passed |
| Desktop example executions, source and source-free Wasm | 154 passed |
| Browser examples, source exports and source-free Wasm exports | 101 passed |
| Sibling marble, painter, snake, PBR and audio demos | All passed on desktop and browser |
| Existing algorithm benchmark smoke profile | All 30 Aquarius/Python/Node cases passed |
| New project's `建置.ps1 -桌面` | Wasm, web folder and standalone Windows EXE built and ran |
| PBR actual GPU pixels | Imported normal direction and metallic roughness alter pixels on both hosts |
| PBR browser presentation | More than 5,000 colors; nested Pages URL, pause key, resize and cleanup passed |
| Runtime source / binary consistency | Pinned Zig `build-wasm-runtime.ps1 -Check` matched |
| Example mirrors and whitespace | Matched; git diff --check passed |

The portable library tests execute parsing/material algorithms in Wasm with
host algorithm callbacks configured to fail if called. Coverage includes numeric
grammar and nonfinite values, UTF-16 indexing, BOM/CRLF/comments, positive and
negative face references, normals/UVs, concave polygon winding and area,
degenerate/nonplanar/self-intersecting input rejection, text/token/face/group
limits, MTL materials and unsupported features, shader uniform layout, aliases,
source-free resources, deterministic linking and reserved module paths.

The broad example tests skip the existing physical-camera demonstrations unless
explicitly enabled; this change does not depend on cameras. Benchmark smoke
inputs establish compatibility and correctness, not a performance ranking.
Browser pixels differ slightly from desktop rasterization; each host checks
the same material/normal relationships rather than byte-identical screenshots.

Reports are retained under `.web-build/pbr/`, `.web-build/projects/`,
`.web-build/browser-results.json`, `.web-build/desktop-example-results.json`
and `AquariusTests/TestResults/pbr-regression-final.trx`. These generated reports
are ignored by Git. The standalone demo is at the sibling
the `AquariusLang_PBR_ModelViewer` repository; its GitHub Pages workflow
deploys its committed web folder when uploaded to a GitHub repository configured
for Actions-based Pages. No remote repository or public deployment was created.
