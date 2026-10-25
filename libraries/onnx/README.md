# onnx — ONNX Runtime inference and the MediaPipe pose tracker

Two modules, with the names, aliases, argument counts, results and error messages of the previous implementation
(contract version 2):

| Module | Functions |
| --- | --- |
| `ONNX` / `推論引擎` / `ONNXRuntime` | `IsSupported`/`是否支援`, `GetProviders`/`取得後端清單`, `CreateSession`/`建立工作階段`, `CreateTensor`/`建立張量`; provider, preference and type constants |
| `ONNX.Session` | `Backend`/`後端`, `Device`/`裝置`, `Providers`/`後端清單`; `GetInputs`/`取得輸入資訊`, `GetOutputs`/`取得輸出資訊`, `Run`/`推論`, `Dispose`/`釋放` |
| `ONNX.Tensor` | `Name`/`名稱`; `GetData`/`取得資料`, `GetShape`/`取得形狀`, `GetType`/`取得型別` |
| `MediaPipe` / `姿態追蹤` | `CreatePoseTracker`/`建立姿態追蹤器` (detector path, landmark path [, provider, preference or provider list]) |
| `MediaPipe.PoseTracker` | `Backend`/`後端`; `Analyze`/`分析` (image, milliseconds), `Reset`/`重設`, `Dispose`/`釋放` |

Device records (`GetProviders`) have `Provider/後端`, `Device/裝置`, `Name/名稱`, `Available/可用`, `Automatic/自動選用`
and `Reason/原因`; metadata records `Name/名稱`, `Type/型別`, `Shape/形狀` (dynamic axes are -1); pose results
`HasPose/有人物`, `Confidence/信心`, `Landmarks/關鍵點` (33 × x, y, z, visibility, presence, or empty),
`TotalMilliseconds/總耗時`, `InferenceMilliseconds/模型耗時` and `DetectionRan/執行人物偵測`. Constants: every provider
(`CPU/中央處理器`, `CUDA/輝達CUDA`, `TensorRT/輝達TensorRT`, `CoreML/蘋果CoreML`, `WebGPU/網頁圖形加速`, `WebNN/網頁神經網路`,
`NNAPI/安卓神經網路`, `XNNPACK/行動處理器加速`, `OpenVINO/英特爾OpenVINO`, `QNN/高通QNN`), the preferences `Auto/自動`,
`GPU/圖形處理器`, `NPU/神經處理器` and the types `Float32/浮點32`, `Float64/浮點64`, `Int32/整數32`, `UInt8/無號整數8`,
`Bool/布林`. A failure stops the script with `推論引擎.<函式>：<原因>` or `姿態追蹤.<函式>：<原因>`.

```aquarius
變數 引擎 = 匯入("推論引擎");
變數 工作 = 引擎.建立工作階段(目前工作目錄 + "/模型.onnx");          # Auto: the best working device, CPU last
變數 輸出 = 工作.推論(["X"], [引擎.建立張量(引擎.浮點32, [2], [1, 2])], ["Y"]);
印出(工作.後端, 工作.裝置, 工作.後端清單, 輸出[0].取得資料());
工作.釋放();
```

## Selection policy

`src/policy.c` is the old `OnnxPlanner`/`OnnxSessionFactory`, the same on every target. Explicit providers (`"CUDA"`,
`["TensorRT", "CUDA"]`) are strict: each must be listed and available here, and an initialization failure is an error.
Preferences fall back: `Auto` tries every available automatic device in rank order and always ends with CPU; `GPU` and
`NPU` try only that class and never become CPU. Accelerators are verified once per process by running a one-node
convolution (the old probe model), so a missing driver or library is reported in `Reason/原因` before a model opens.
A session's `Providers/後端清單` is the chain handed to the engine, including companions (CUDA behind TensorRT) and the
implicit CPU fallback. The pose tracker resolves the selection for its detector and opens the landmark model on exactly
the same chain. Limits: 8 live sessions and 4 trackers per runtime, 16 axes and 16,777,216 elements per tensor,
256 MiB per model, pose frames up to 1920 × 1080 (portrait too).

## Platforms

Ranked devices per target are one table, `src/devices.def`; adding a provider to a target is one row there.

| Target | Ranked devices (Auto order) | Engine |
| --- | --- | --- |
| Windows x64 | CUDA¹ · OpenVINO GPU² · WebGPU (Dawn on D3D12) · OpenVINO NPU² · TensorRT¹³ · OpenVINO CPU²³ · CPU | ONNX Runtime 1.30.0 release + WebGPU plugin EP 0.4.0 |
| Linux x64 | the same (WebGPU through Vulkan) | ONNX Runtime 1.30.0 release + WebGPU plugin EP 0.4.0 |
| Android arm64-v8a, x86_64 | WebGPU (Vulkan) · NNAPI · XNNPACK · CPU⁴ | ONNX Runtime 1.30.0 Android package (AAR) |
| Web | WebGPU⁵ · WebNN GPU⁵ · WebNN NPU⁵ · CPU (WebAssembly SIMD, one thread) | ONNX Runtime 1.30.0 built from source into the side module ([Web](#web)) |

¹ Only with ONNX Runtime's CUDA 13 build (`-DAQUA_ONNX_CUDA=ON`); the standard build lists CUDA and TensorRT as
unavailable with instructions. NVIDIA's CUDA 13 runtime, cuBLAS and cuDNN 9 (and TensorRT 10 for TensorRT) are not
redistributed here: on Windows they are found in `onnx-providers/cuda/` beside the program, the absolute folders of
`AQUARIUS_ONNX_CUDA_PATH`, the program's folder or `CUDA_PATH` (flat, toolkit `bin`/`bin/x86_64` and NVIDIA Python
package `cu13/bin/x86_64`/`cudnn/bin` layouts); on Linux through the system loader. `AQUARIUS_ONNX_CUDA_DEVICE` picks
the GPU (default 0); TensorRT caches engines in `$AQUARIUS_ONNX_CACHE/tensorrt` or
`%LOCALAPPDATA%/AquariusLang/onnx/tensorrt` (`~/.local/share/...` on Linux).
² Optional plugin: put `onnxruntime_providers_openvino_plugin.dll`/`libonnxruntime_providers_openvino_plugin.so` and
its OpenVINO libraries beside the program, in an `onnx-providers/` folder there, or in a folder listed in
`AQUARIUS_ONNX_PROVIDER_PATH` (searched the same way for any plugin).
³ Explicit only (TensorRT builds an engine for seconds to minutes on first use).
⁴ On the Android emulator WebGPU, NNAPI and XNNPACK are used only on explicit request.
⁵ Listed as unavailable with the reason ([Web](#web)), so explicit requests fail clearly and preferences fall back.

Tuning is the old one: full graph optimization, sequential execution, no idle spinning; accelerated sessions run CPU
fallback nodes on one thread; CUDA grows its arena by exact requests with exhaustive cuDNN search; TensorRT uses FP16
with engine and timing caches; NNAPI runs accelerators only; XNNPACK owns its pool; Android's CPU provider uses half the
cores up to four. Software GPU adapters (WARP, llvmpipe, SwiftShader) stay listed but are used only on explicit request.
`AQUARIUS_ONNX_DIAGNOSTICS=1` prints the probed device table on stderr.

## Runtime files and packaging

ONNX Runtime is loaded at first use (`src/platform`), so the runtime starts and applications that never import ONNX
run without it. `CMakeLists.txt` downloads the pinned packages (into `AQUA_ONNX_DOWNLOADS`, default
`<build>/downloads`; point several build directories at one folder to download once), copies the runtime files next to
the built runtime or external library, and installs them beside it with `cmake --install`. The exporter ships these
files next to applications that use the library:

| Target | Files beside the executable / in the APK's `lib/<abi>/` | Licenses (`licenses/`) |
| --- | --- | --- |
| Windows x64 | `onnxruntime.dll`, `onnxruntime_providers_shared.dll`, `onnxruntime_providers_webgpu.dll`, `dxcompiler.dll`, `dxil.dll` (+ `onnxruntime_providers_cuda.dll`, `onnxruntime_providers_tensorrt.dll` with CUDA) | `onnxruntime/`, `onnxruntime-webgpu/` |
| Linux x64 | `libonnxruntime.so.1`, `libonnxruntime_providers_shared.so`, `libonnxruntime_providers_webgpu.so` (+ CUDA/TensorRT provider libraries) | `onnxruntime/`, `onnxruntime-webgpu/` |
| Android | `libonnxruntime.so` (16 KiB page aligned) | ONNX Runtime is MIT licensed (the AAR carries no notice file) |

Windows: the official DLLs need the Microsoft Visual C++ 2015–2022 runtime (x64), as with the old .NET host; a missing
runtime is reported as the CPU device's reason. ONNX Runtime converts plugin paths to the ANSI code page, so the
library hands it the 8.3 short path when a folder name is outside that code page; embedding a UTF-8 active-code-page
manifest in the executable (as the old host did) removes the limitation on volumes without short names. Linux: the
WebGPU plugin needs a Vulkan loader (`libvulkan.so.1`).

Models are read from the application package (`--assets`; by package path or by `目前工作目錄 + "/..."`) or, on native
targets, from the file system. `CreateSession` and `CreatePoseTracker` also accept the model's bytes as an array of
integers, and `CreateTensor` accepts an image object as the data of a `uint8` tensor (its RGBA bytes); both are
additions.

## Web

The web runs the same C code; `src/platform/web.c` links ONNX Runtime instead of loading it. `cmake/onnxruntime-web.cmake`
downloads ONNX Runtime's source release and the dependencies its CMake fetches (each pinned by SHA-256 and handed to it
as a local mirror), applies `patches/` (explained in `patches/README.md`) and builds the CPU execution provider with
the runtime's web toolchain: `wasm32-wasip1`, position independent, MLAS's WebAssembly SIMD kernels, no exceptions, no
threads, no Emscripten and no JavaScript. It is a full build (ONNX models, contrib and traditional-ML operators), so the
old examples' models run unchanged; the libraries link into `onnx.wasm` (about 17 MB, 3.8 MB gzip-compressed;
symbols private, names stripped). The build runs once per build directory at configure time and takes about ten
minutes; set `AQUA_ONNX_WEB_ORT_BUILD` to an existing `onnxruntime-web` build directory to reuse one.

In Chromium on the showcase's person photo the pose tracker gives the native CPU result (confidence
0.9999743700027466, coordinates within 1e-6) in about 100–130 ms for the first frame (detector and landmarks) and
33–38 ms per tracked frame.

Listed as unavailable on the web, with these reasons:

- **WebGPU.** ONNX Runtime waits for GPU readbacks inside a run. A page cannot block on a WebGPU promise, and the web
  runtime has neither threads nor stack switching (JSPI) that would let the engine wait. ONNX Runtime's WebGPU provider
  is also written against Dawn's C++ API (Dawn-only structures and toggles), not the standard `webgpu.h` that
  `web-glue/webgpu` implements. (The old browser host used onnxruntime-web's JavaScript execution providers.)
- **WebNN**, which ONNX Runtime implements in JavaScript.

Without exceptions, an error ONNX Runtime reports by throwing (a kernel's consistency check on a malformed model) ends
the application with a console message instead of failing the call.

## Dependencies

| Dependency | Version | License | Pinned |
| --- | --- | --- | --- |
| ONNX Runtime (Windows x64, Linux x64 releases; CUDA 13 variants) | 1.30.0 | MIT | GitHub release archives, SHA-256 in `CMakeLists.txt` |
| ONNX Runtime Android (AAR) | 1.30.0 | MIT | Maven Central, SHA-256 in `CMakeLists.txt` |
| ONNX Runtime WebGPU plugin EP (`Microsoft.ML.OnnxRuntime.EP.WebGpu`, includes DXC on Windows) | 0.4.0 | MIT (+ notices) | nuget.org, SHA-256 in `CMakeLists.txt` |
| ONNX Runtime source (web) | 1.30.0 | MIT | GitHub tag archive, SHA-256 in `cmake/onnxruntime-web.cmake`; `patches/` |
| Its dependencies (web): Abseil, date, Eigen, FlatBuffers, GSL, Boost.Mp11, nlohmann/json, ONNX, protobuf (+ host `protoc`), RE2, SafeInt | as ONNX Runtime 1.30.0's `cmake/deps.txt` | Apache 2.0, MIT, MPL 2.0, BSD, Boost (listed in ONNX Runtime's installed `ThirdPartyNotices.txt`) | official GitHub archives, SHA-256 in `cmake/onnxruntime-web.cmake` |

Downloads verify TLS. The Strawberry Perl CMake has no certificate store: configure it with
`-DCMAKE_TLS_CAINFO=<ca-bundle.crt>` (for example Git for Windows' `mingw64/etc/ssl/certs/ca-bundle.crt`) or place the
pinned files in `AQUA_ONNX_DOWNLOADS` beforehand; their hashes are checked either way.

## Differences from the old implementation

- **No C#, NuGet or JavaScript**: the native targets use ONNX Runtime's C API from the official prebuilt releases; the
  Android Java adapter and its JSON/base64 transport are gone (tensors cross in memory). `pose.c` is compiled into the
  library (state per tracker instead of a Wasm instance per tracker) and keeps plain IEEE single precision (no fused
  multiply-add), so landmarks match on every target; CPU results equal the old ones (confidence 0.9999743700027466 on
  the showcase's person photo).
- **Records are native objects**: sessions, tensors, device, metadata and pose records keep their fields and methods,
  but `typeOf` reports `onnx.session`, `onnx.tensor`, `onnx.device`, `onnx.valueinfo`, `mediapipe.posetracker` and
  `mediapipe.poseresult` instead of `module`. Argument-count and argument-type errors for the declared signatures come
  from the runtime; the library's own checks keep the old messages.
- **Messages that only existed because of .NET**: a missing model file reports `找不到模型檔案「…」。` instead of .NET's
  `Could not find file`; a non-image `Analyze` argument reports `必須傳入影像物件。` instead of `Expected a resource owned
  by this application runtime.`; a released tracker reports `姿態追蹤器已關閉。` instead of `ObjectDisposedException`'s
  text; the CUDA opt-in reason names `-DAQUA_ONNX_CUDA=ON` instead of `-p:AquariusOnnxCuda=true`.
- **CUDA packaging**: the old CUDA runtime packs embedded NVIDIA's wheels through a Python script; that script is gone
  (no Python). The CUDA build of ONNX Runtime is still selected per build (`AQUA_ONNX_CUDA`, which the exporter's
  `[onnx] cuda = true` / `--onnx-cuda true` should select), and NVIDIA's libraries are supplied as described above.
  NuGet's `runtimes/<rid>/native` plugin layout is no longer searched.
- **WebGPU adapter choice**: on machines with several GPUs the plugin's discrete GPU is preferred (like the browser's
  `high-performance` request); the old desktop host took the first adapter, often the integrated one.
- **Not available**: DirectML (only in a separate NuGet package, not in the official release archives; the old
  implementation excluded it deliberately), Core ML and QNN (macOS and Windows on Arm are not targets yet; each is one
  row in `devices.def` when they are), and WebGPU and WebNN on the web (see [Web](#web): the web runs ONNX Runtime's
  CPU provider, one thread, instead of onnxruntime-web). Sessions are no longer serialized against Dawn: every call
  runs on the runtime's thread.

## Tests

`tests/<case>.aqua` with the exact expected stdout in `tests/<case>.expected`:

- `inference` — a model the test writes itself (a small protobuf writer in Aquarius): all five tensor types, a dynamic
  axis, several outputs in requested order, metadata, session records and release.
- `providers` — constants, the ranked device list's invariants, bilingual selection parsing, Auto with the old tests'
  `add.onnx` bytes.
- `pose` — needs the Images library and the MediaPipe models as `models/` (packaged with `--assets models`, or on
  native targets a folder beside the test application): `人物偵測.onnx`
  ([OpenCV Zoo person_detection_mediapipe_2023mar](https://github.com/opencv/opencv_zoo/tree/8e2b658d1007864a24cc2cf0e5b4288c74b32584/models/person_detection_mediapipe),
  SHA-256 `47fd5599…a701f`) and `姿態.onnx` ([pose_estimation_mediapipe_2023mar](https://github.com/opencv/opencv_zoo/tree/1f19f821d68288feff2ef5c53993b33da74b1509/models/pose_estimation_mediapipe),
  SHA-256 `9d89c599…2f3f`), Apache 2.0.
