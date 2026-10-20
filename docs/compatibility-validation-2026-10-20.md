# Compatibility validation — 2026-10-20

The current working tree was tested against 20 existing sibling projects and the four ONNX showcases. Existing local ONNX changes were preserved. Tests use the rebuilt Release compiler and the freshly published standard and CUDA Windows runtime packs. No projects were migrated to a different language or replaced with mock applications.

## CUDA packaging for application developers

CUDA inference dependency packaging is now part of the general executable exporter. Set this once in an application's root `aqua.toml`:

```toml
[onnx]
cuda = true
```

Then `aqua build . --target windows` selects the CUDA runtime pack. That pack embeds ONNX Runtime, NVIDIA CUDA 13, cuBLAS, NVRTC, cuDNN 9, the Microsoft runtime libraries and license notices. The delivered EXE extracts its private runtime automatically. Recipients need no Python, CUDA toolkit installation, separate DLL folder, checkout or PATH modification. A one-off `--onnx-cuda false` exports the standard runtime instead.

The compiler distribution must include the optional CUDA pack. Toolchain maintainers prepare it once with `scripts/publish-apphost.ps1 -OnnxCuda`; this downloads and verifies SHA-256-pinned NVIDIA wheels. Normal application exports reuse that pack offline. A provider-only pack from before this change is rejected with instructions to republish it. `scripts/publish-cli.ps1 -OnnxCuda` and the release packaging path carry the same libraries for `aqua run`.

This covers ONNX CUDA inference dependencies. The complete toolkit/compiler and the separate TensorRT SDK are not bundled. The recipient supplies supported NVIDIA hardware and a compatible driver. CUDA 13 uses the R580-or-newer driver family, subject to NVIDIA's feature and library requirements; see [NVIDIA compatibility](https://docs.nvidia.com/deploy/cuda-compatibility/minor-version-compatibility.html) and [ORT's CUDA/cuDNN matrix](https://onnxruntime.ai/docs/execution-providers/CUDA-ExecutionProvider.html). `Auto` falls back when CUDA is unavailable; explicit CUDA requests report the failure. CPU remains usable. This cannot make unsupported GPUs or drivers support CUDA.

The generic packaging proof builds an unrelated manifest project without showcase scripts. It copies only EXEs to a temporary path containing Chinese characters and spaces, uses another Chinese path for private runtime extraction, clears CUDA/compiler discovery variables, limits PATH to Windows directories, and runs from `C:\Windows`. Real CUDA inference and the explicit standard-runtime CPU override passed. The same proof also exercises CPU and `Auto` in the CUDA-enabled EXE with CUDA devices hidden from the child process. This simulates CUDA unavailability; it is not a physical GPU-less machine test.

The four delivered ONNX CUDA EXEs were rebuilt without sidecar DLL folders. Ordinary short button clicks completed CPU → CUDA → CPU in all four apps while all four windows remained open. Eleven packaged inference/off/parity checks and all four refreshed browser scenarios passed. The classifier also passed CUDA and CPU inference after relocation with an isolated PATH.

## Existing project coverage

Every row below passed the stated checks unless a browser limitation is explicitly recorded. “Web export” means a successful build; actual browser execution is stated separately.

| Existing project | Checks performed |
| --- | --- |
| Marble game | Desktop Wasm and EXE rendering; integration assertions; Chrome WebGPU rendering, pause/keyboard/resize and cleanup |
| Painter | Desktop Wasm and EXE rendering; Chrome WebGPU rendering and cleanup |
| Snake | Desktop Wasm and EXE rendering; 236 rule assertions; Chrome WebGPU rendering and cleanup |
| PBR example | Desktop Wasm and EXE rendering; GPU assertions; Chrome WebGPU rendering/assertions |
| Audio workshop | Desktop Wasm and EXE; audio assertions; Chrome rendering/audio assertions |
| MediaPipe examples | Desktop Wasm and EXE rendering; model inference assertions; web export |
| Single-user Kanban | Desktop Wasm and EXE rendering; web export |
| PatchCore | Desktop Wasm and EXE; project assertions; web export |
| Advanced PBR | Desktop Wasm and EXE rendering; project assertions; web export |
| Sponza Atrium | Desktop Wasm and EXE rendering; project assertions; web export |
| Bottle showcase | Real cached asset and dynamic-library package in Wasm and EXE; web export |
| OpenCV showcase | 45 feature checks and bilingual checks in Wasm and separate EXEs; desktop UI; web export |
| Java/FastCSV bridge | Real in-process Java calls, Unicode, arrays, exceptions and CSV round trip in Wasm and EXE |
| ImageJ ColorInspector3D | Original relative JDK/JAR deployment; validation and 36-stage native WGPU GUI scenario, including ROI, TIFF, AVI, DICOM and large-image paths |
| Filament showcase | All five native scenes in Wasm and EXE; all five browser scene captures with software WebGL; default Intel WebGL failure recorded below |
| Dynamic linking | Real native DLL and portable Wasm plugin calls in Wasm and EXE; web export |
| AquariusDreamer | Main application compilation; 289 offline Markdown/layout assertions |
| CRDT/WebRTC canvas | Real standalone server and three concurrent native clients: edits, convergence, presence and P2P; standalone client UI; web export |
| NetworkLab | Real standalone server: HTTP, JSON, cookies and 422 handling; standalone client UI; web export |
| MineClone | Full isolated survival scenario in the current source host, final standalone EXE and Chrome WebGPU: mining, crafting, furnace, chest, night and inventory save/load parity; 13 fresh native captures |

Dreamer tests make no live paid AI requests. MineClone uses a disposable copy of the project's own debug script and bypasses preference reads, leaving the user's saved world and settings untouched. Network tests start and stop only their own servers and refuse occupied ports. Inspector retains its original relative JDK deployment layout.

## Language/runtime fixes and test results

MineClone exposed missing WGPU window APIs in the ONNX development branch. The existing local WGPU window/input implementation was integrated, including window-backed devices, retained vertex buffers, procedural draws, render state, storage bindings and browser input/presentation. Duplicate `ReadRange`/`WriteRange` catalog entries were removed, preserving their original portable declarations.

ONNX UI testing exposed short clicks lost between Processing polls and GLFW interpreting a foreign process's window property as a local pointer when input queues were attached. Processing now uses sticky mouse presses, and the Win32 bridge checks active windows against its owned GLFW window list. All four CUDA interfaces passed together after the native bridge and both runtime packs were rebuilt.

The C# suite passed **1,834 tests**, with **5 skipped** optional OpenGL/attached-window/platform cases and zero failures. The complete web JavaScript suite passed **425 tests**. The shared Android JavaScript suite passed **98 tests**. The classic project harness recorded **28 passed checks**, the extended harness **16**, and the two networking projects **4**, in addition to MineClone and the browser scenarios.

Evidence is retained under `.web-build/`:

- `compatibility/dotnet/compatibility-final.trx`
- `projects/desktop-results.json` and `projects/browser-results.json`
- `compatibility/extended-results.json` and `compatibility/network/results.json`
- `compatibility/mineclone/results.json`, `source-results.json`, `survival.txt`, and `cap-*.png`
- `compatibility/browser-results.json` and `compatibility/filament/browser-captures/`
- `cuda-packaging-proof/results.json`
- `onnx-showcase/cuda-fix/` for compiler publishing and complete test logs

The ONNX delivery has its own `VALIDATION.md`, provider outputs, device reports, SHA-256 manifests and UI/emulator evidence under `C:\OfficialProjects\AquariusLangTW_ONNX_Showcase`.

## Reproduction

Use a Release compiler, prepared browser dependencies, native bridges and both runtime packs. Select an installed .NET 8 SDK if `dotnet` is not on PATH. The extended scripts use PowerShell 7.

```powershell
dotnet test AquariusTests -c Release
npm.cmd test --prefix AquariusWebCompiler
node --test AquariusAndroid/tests/*.test.mjs
./scripts/test-cuda-packaging.ps1
./scripts/test-projects.ps1 -Configuration Release -SkipBuild -WindowsExecutables
./scripts/test-extended-projects.ps1
./scripts/test-network-showcases.ps1
./scripts/test-mineclone.ps1
node AquariusWebCompiler/tests/project-smoke.mjs
$env:WEBGL_ANGLE='swiftshader'
node scripts/test-compatibility-browser.mjs
```

Finish exports before running browsers against those directories. Browser tests require localhost access. To reproduce the Intel limitation, omit `WEBGL_ANGLE`; do not change system GPU or security settings.

## Observed limits

The default Chrome WebGL renderer here is Intel Iris Xe through ANGLE/D3D11. Filament's `base_lit_masked` shader fails linking and loses the context on that path. The identical exported application rendered all five scenes with SwiftShader, and native Filament passed. This remains an unresolved driver/rendering-library compatibility issue; software rendering in the validation harness is not an automatic application fallback and does not prove the default Intel browser path works.

Windows testing used an RTX 4070 Laptop GPU with driver 616.92. The ONNX apps' Android provider/UI and synthetic-camera evidence comes from the API 35 x86_64 emulator runs during this work; the later shared JavaScript checks passed as stated above. Existing desktop showcases were not all ported to Android. Physical ARM Android, vendor NNAPI, Linux/macOS CUDA runtime execution, other browsers and every GPU/driver combination were not validated. Linux CUDA payload preparation is implemented, but only Windows CUDA embedding was executed here.
