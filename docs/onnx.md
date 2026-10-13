# ONNX Runtime

Core defines `IOnnxBackend`, `IOnnxSession`, owned `OnnxTensor` snapshots, immutable
value metadata, provider options, limits, error kinds and the bilingual catalog.
It references no ONNX Runtime package, GPU API, JavaScript engine or platform library.
New hosts implement contract version 1 and register the same language surface;
compiler lowering and application scripts do not branch on platform.

Desktop uses Microsoft.ML.OnnxRuntime **1.30.0** with the official
Microsoft.ML.OnnxRuntime.EP.WebGpu **0.4.0** plugin. The process registers the plugin
once, selects a WebGPU device and explicitly appends it to session options. ORT's
Dawn device is independent of the graphics host's wgpu-native device; their native
handles must never be interchanged. Supported plugin RIDs are win-x64, win-arm64,
linux-x64, linux-arm64 and osx-arm64. Unsupported systems can explicitly select CPU.
Windows executable hosts embed a UTF-8 active-code-page manifest, avoiding the native
plugin bridge's code-page conversion failure for Traditional Chinese deployment
paths. This requires Windows 10 version 1903 or later. Custom embedding hosts need
the same manifest when native library paths contain Unicode characters; use the
manifested `aqua.exe` in such deployments. `AQUARIUS_ONNX_DIAGNOSTICS=1` enables native
initialization exception details on stderr for development.

Browser uses onnxruntime-web **1.30.0**, its WebGPU module and matching Asyncify
Wasm/glue, packaged locally from pinned npm dependencies by `npm run prepare:browser`.
Core owns the deployment descriptor, runtime version, embedded browser assets and
license notices. Exports include hashes and need no CDN. CPU selects the Wasm EP;
one Wasm thread avoids cross-origin isolation requirements. WebGPU requires a
compatible browser, driver and secure context (HTTPS or localhost).

ONNX is a documented native integration under the host-capability profile: desktop
executes ORT kernels in the native runtime and Dawn, whereas browser kernels execute
in ORT Wasm/WebGPU. This specific integration is an exception to the general future
computational-library Wasm rule; it must not be labelled `CoreWasm`. Portable demo
preprocessing and postprocessing remain compiled Aquarius Wasm. The profile does
not grant a general exception to other computational libraries.

## Language API

Import `ONNX`, `ONNXRuntime` or `推論引擎`. Both names below reference the same
callable. Metadata fields and constants also have both spellings.

| Object | English / 繁體中文 | Arguments and result |
| --- | --- | --- |
| ONNX | IsSupported / 是否支援 | provider → capability availability |
| ONNX | CreateSession / 建立工作階段 | bundled model path, provider → session |
| ONNX | CreateTensor / 建立張量 | type, shape, flat values → tensor |
| Session | GetInputs / 取得輸入資訊 | → array of Name/名稱, Type/型別, Shape/形狀 records |
| Session | GetOutputs / 取得輸出資訊 | → same metadata |
| Session | Run / 推論 | input names, tensors, requested output names → ordered tensor array |
| Session | Dispose / 釋放 | → idempotent release |
| Tensor | GetType / 取得型別 | → type string |
| Tensor | GetShape / 取得形狀 | → dimension array |
| Tensor | GetData / 取得資料 | → independent flat array |

Session `Backend/後端` reports the selected EP (`WebGPU` or `CPU`); output tensors
have `Name/名稱`. Provider constants: `WebGPU/網頁圖形加速`, `CPU/中央處理器`.
Type constants: `Float32/浮點32`, `Float64/浮點64`, `Int32/整數32`,
`UInt8/無號整數8`, `Bool/布林`. Version 1 deliberately admits only those five types;
int64, float16, strings, sequences, maps and optional inputs require future contract
extensions. Unsupported metadata is visible as `unsupported` and cannot be supplied
or materialized as a tensor. Dynamic dimensions appear as -1.

```text
變數 引擎 = 匯入("推論引擎");
變數 工作 = 引擎.建立工作階段(目前工作目錄 + "/模型.onnx", 引擎.網頁圖形加速);
變數 資料 = 引擎.建立張量(引擎.浮點32, [2], [1, 2]);
變數 輸出 = 工作.推論(["input"], [資料], ["output"]);
變數 張量 = 輸出[0];
印出(張量.取得資料());
工作.釋放();
```

Include models with CLI `--assets`; website source-directory builds also recognize
`.onnx`. Model external-data sidecars are not supported in version 1: use a single
self-contained ONNX file. Model input and output names are case-sensitive.

## Execution, ownership and extension rules

* WebGPU is the only admitted GPU provider. CPU is explicit. Failure to initialize
  the selected EP never opens a replacement CPU session. ORT can still execute
  individual unsupported WebGPU operators on its CPU EP, as upstream ORT does;
  the backend field does not claim every node ran on GPU.
* All required inputs must appear exactly once, with matching type, rank and static
  dimensions. Requested outputs must be known, unique and nonempty. Ordering follows
  the request, independent of provider dictionary order.
* Tensors belong to their binding runtime. Keys use module scope identity so guest
  reprojection and garbage collection preserve ownership. Weak tables avoid rooting
  dead tensors; extracted methods retain their data. Copies prevent caller mutation
  or disposed native results from invalidating existing tensors.
* Sessions own native resources until explicit release or host disposal. Maximum
  eight live sessions, sixteen tensor axes, 16,777,216 elements per tensor and
  256 MiB per model. GPU outputs are downloaded and copied before releasing upstream
  tensors. Limits apply at the boundary; ORT's own internal allocations depend on
  model graph and device. Sessions cannot mix tensors from other binding runtimes.
* Native GPU discovery, session creation, runs and disposal share a process-wide
  gate around Dawn provider state; CPU runs on separate sessions remain concurrent.
  GPU sessions use one CPU thread for fallback operations, and idle native thread
  pools do not spin, leaving CPU time for camera capture and drawing.
  Native runs are serialized with disposal. Cancellation terminates native run
  options; native model creation can only observe cancellation before/after the
  synchronous engine call. Browser runs reject overlap. Browser cancellation is
  observed at async boundaries; upstream cannot forcibly abort an in-flight run.
  Disposal waits for the complete run/download/cleanup before releasing the session.
  Late session creation after cancellation/disposal releases its native resources.
* New providers require a deliberate contract/catalog update and parity tests;
  new platforms only need a host adapter and composition. GPU buffer interoperability,
  external model data and additional element types need explicit ownership/version
  designs before extending this CPU-snapshot contract.

## MediaPipe pose pipeline

`MediaPipe` / `姿態追蹤` is a separate, model-specific library composed over the
generic ONNX contract. `CreatePoseTracker` / `建立姿態追蹤器` takes the detector
path, landmark path and explicit `CPU` or `WebGPU` provider. A tracker owns both
sessions and one isolated 16 MiB Wasm instance; at most four trackers per runtime.
`Analyze` / `分析` takes an application image handle and a nonnegative, monotonic
timestamp in milliseconds since capture started. Input limits are 1920 × 1080.
`Reset` / `重設` discards tracking and smoothing state; `Dispose` / `釋放` is
idempotent. Calls on a disposed tracker fail. One tracker processes one frame at
a time. Images and tensors remain runtime-owned; no scalar pixel arrays cross
the language boundary.

Result fields have English/Traditional Chinese aliases: `HasPose` / `有人物`,
`Confidence` / `信心`, `Landmarks` / `關鍵點`, `TotalMilliseconds` / `總耗時`,
`InferenceMilliseconds` / `模型耗時`, `DetectionRan` / `執行人物偵測`.
Landmarks are a flat 33 × 5 array: x, y, z, visibility, presence. x/y refer to the
original unmirrored frame, normalized by width/height; z uses frame width and
visibility/presence are sigmoid probabilities. Missing poses return an empty
array immediately, preventing stale skeletons. Display thresholds remain an
application decision; the example requires 0.75 and hides points outside the frame.

`AquariusCore/mediapipe/pose.c` is the sole implementation of letterboxing,
bilinear affine crops, 2254-anchor decoding, weighted suppression around the
strongest detection, 1.25 ROI expansion, heatmap refinement, inverse projection,
ROI tracking and adaptive One Euro filtering. Compile its committed `pose.wasm`
with `scripts/build-pose-wasm.ps1` (pinned Zig 0.15.1). C#/JavaScript adapters only
transfer bulk buffers and call the injected ONNX backend. Future hosts therefore
reuse this Wasm and implement the existing image, Wasm and ONNX contracts.
Single-person tracking periodically reacquires and reacquires after loss; it
does not associate identities across multiple people.

The contracts follow [MediaPipe postprocessing](https://github.com/google-ai-edge/mediapipe/blob/master/mediapipe/modules/pose_landmark/tensors_to_pose_landmarks_and_segmentation.pbtxt)
and [BlazePose ROI configuration](https://github.com/tensorflow/tfjs-models/blob/master/pose-detection/src/blazepose_tfjs/constants.ts).

## Validation

`dotnet test AquariusTests/AquariusTests.csproj --filter FullyQualifiedName~AquariusTests.Onnx`
tests real native CPU inference, bilingual Wasm binding, metadata, shapes and lifetime.
Set `AQUARIUS_ONNX_WEBGPU=1` to require native GPU inference. Set
`AQUARIUS_ONNX_POSE_MODEL` to a MediaPipe model path to require real pose CPU/GPU
output comparison (absolute tolerance 0.05 plus relative 1%, for floating-point EP
differences). The tiny checked-in Add fixture is an ONNX IR 8/opset 13 model with
two float32 vectors; it contains no external model data.

`npm test --prefix AquariusWebCompiler` includes browser contract, cancellation,
disposal and real Aquarius Wasm binding tests. `node AquariusWebCompiler/tests/onnx-browser.mjs`
checks a built MediaPipe project in Chromium: real CPU/WebGPU inference, nested
deployment URLs, numerical parity, camera preview and cleanup. See
[the Traditional Chinese demo](../examples/mediapipe/README.md).

Set `AQUARIUS_POSE_FIXTURES` to a directory containing upstream MediaPipe
`pose.jpg`, `pose_landmarks.pbtxt`, and the pinned `人物偵測.onnx` to enable real
person tests. They verify all 33 reference points (mean error <18 px), 90-degree
rotation, off-center framing, continuous ROI tracking, blank rejection and
CPU/WebGPU parity. `AQUARIUS_POSE_GUI=1` runs the actual desktop example with a
simulated camera, saves a screenshot and verifies shutdown and >20 fps. The
browser test optionally consumes `pose.y4m`, the native JSON results and a
`closeup.png` fixture to test real camera display and sustained frame rate.

Upstream references: [native WebGPU EP](https://onnxruntime.ai/docs/execution-providers/WebGPU-ExecutionProvider.html),
[ORT WebGPU](https://onnxruntime.ai/docs/tutorials/web/ep-webgpu.html),
[session API](https://onnxruntime.ai/docs/api/js/interfaces/InferenceSession.html).
