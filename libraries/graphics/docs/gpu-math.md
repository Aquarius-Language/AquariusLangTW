# GPU Mathematics / GPU 數學

`GPUMath` / `GPU數學` is a core library compiled into the application's WebAssembly.
It accepts caller-defined WGSL expressions, functions and complete compute shaders.
Algorithms are supplied by the application; platform hosts only supply WebGPU.

`GPUMath` / `GPU數學` 是編譯進應用程式 WebAssembly 的核心函式庫。
應用程式可傳入 WGSL 運算式、含迴圈及輔助函式的規則，或完整計算著色器。
計算規則不受固定數學函式清單限制，平台主機只負責 WebGPU 資源與派送。

## Choice of language / 語言選擇

WGSL is the browser WebGPU shader language. Using it directly avoids separate GLSL,
HLSL or CUDA compilers and inconsistent platform translations. The expression API
embeds the caller's expression into WGSL; it is not an Aquarius closure compiler.
Existing GLSL/HLSL shaders must first be converted to WGSL by the caller's tooling.

WGSL 是瀏覽器 WebGPU 使用的著色語言；直接採用它可共用同一套規則，並避免維護
多套平台轉譯器。運算式介面會將輸入嵌入 WGSL，不會將任意星泉閉包轉成 GPU 程式。
已有 GLSL / HLSL 時，請先以既有工具轉成 WGSL。

References / 參考：[WGSL specification](https://www.w3.org/TR/WGSL/),
[WebGPU specification](https://www.w3.org/TR/webgpu/).

## Bilingual API / 雙語介面

| English | 繁體中文 | Arguments / 參數 |
| --- | --- | --- |
| CreateDevice | 建立裝置 | none / 無 |
| Expression | 運算式 | WGSL expression, input lengths, output count / 運算式、輸入長度陣列、輸出數量 |
| CreateKernel | 建立核心 | WGSL functions containing `fn rule(index:u32)->f32`, lengths, count / 規則、輸入長度陣列、輸出數量 |
| CreateBuffer | 建立緩衝區 | device, kernel, input arrays / 裝置、核心、輸入陣列群 |
| WriteInput | 寫入輸入 | kernel, buffer, input index, values / 核心、緩衝區、輸入索引、資料 |
| Dispatch | 派送 | device, kernel, buffer / 裝置、核心、緩衝區 |
| ReadResult | 讀取結果 | kernel, buffer / 核心、緩衝區 |
| Calculate | 計算 | device, kernel, input arrays / 裝置、核心、輸入陣列群 |
| DispatchWGSL | 派送WGSL | device, complete WGSL, buffer, workgroups / 裝置、完整 WGSL、緩衝區、工作群組數 |

Functions share the same compiled function identity in both languages. Kernel
records contain `ok` / `成功`, `error` / `錯誤`, and `source` / `原始碼` aliases.
Inspect `ok` / `成功` before allocating buffers for a kernel.

兩種語言名稱使用相同編譯函式。核心記錄提供 `ok` / `成功`、`error` / `錯誤`、
`source` / `原始碼`；建立緩衝區前請檢查 `ok` / `成功`。

```aqua
變數 數學=匯入("GPU數學");
變數 裝置=數學.建立裝置();
變數 核心=數學.運算式("input0(index)*input0(index)+sin(input1(index))",[3,3],3);
變數 緩衝區=數學.建立緩衝區(裝置,核心,[[1,2,3],[0,0,0]]);
數學.派送(裝置,核心,緩衝區);
印出(數學.讀取結果(核心,緩衝區));
數學.寫入輸入(核心,緩衝區,1,[1,1,1]);
數學.派送(裝置,核心,緩衝區);
印出(數學.讀取結果(核心,緩衝區));
緩衝區.釋放();裝置.釋放();
```

```aqua
變數 math=匯入("GPUMath");
變數 device=math.CreateDevice();
變數 kernel=math.CreateKernel("fn rule(index:u32)->f32 {
    var sum=0.0;
    for(var k=0u;k<input0Length;k++){sum+=input0(k);}
    return sum+input1(index);
}",[3,2],2);
印出(math.Calculate(device,kernel,[[1,2,3],[10,20]]));
device.Dispose();
```

## Storage and execution / 儲存與執行

The portable ABI uses a single `@group(0) @binding(0)` storage arena of `array<f32>`.
Inputs occupy disjoint ranges followed by the output range. Generated `input0(i)`,
`input1(i)`… helpers and `input0Length`… constants expose those ranges. The generated
entry point uses 64 invocations per workgroup and guards the final partial workgroup.
Each `rule` must produce one independent result, read valid input indices and avoid
cross-invocation writes. A multi-stage algorithm can dispatch multiple kernels on
the same arena. Use `DispatchWGSL` with your own `cs_main` for barriers, workgroup
memory, custom workgroup sizes or in-place algorithms; synchronization and bounds
then belong to that shader.

可攜 ABI 使用單一 `@group(0) @binding(0)`、`array<f32>` 儲存緩衝區。
各輸入使用不重疊範圍，輸出位於尾端。自動產生 `input0(i)`、`input1(i)` 等函式，
以及 `input0Length` 等長度常數。每個工作群組有 64 個執行緒，尾端自動檢查範圍。
規則必須獨立計算一個結果、讀取有效索引，並避免跨執行緒寫入。
需要同步屏障、工作群組記憶體、自訂執行緒數或原地計算時，以 `派送WGSL` 傳入
自有 `cs_main`，由著色器負責同步與索引範圍。

Buffers remain on the GPU across dispatches. `WriteInput` uploads only the changed
range and `ReadResult` downloads only the result range. `WGPU.Buffer.WriteRange` /
`寫入範圍` and `ReadRange` / `讀取範圍` are also available directly. Desktop, browser
and Android cache at most 32 compute pipelines per device. `Calculate` is a
one-shot convenience; repeated workloads should retain a device/kernel/buffer.
Readback synchronizes preceding GPU work; dispatch by itself does not report elapsed
GPU execution time.

緩衝區在多次派送之間保留於 GPU。`寫入輸入` 只上傳異動範圍；`讀取結果` 只下載
結果範圍。WGPU 緩衝區也可直接呼叫雙語範圍讀寫函式。各平台每個裝置最多快取
32 個計算管線。重複工作應保留裝置、核心與緩衝區；`計算` 用於單次操作。
讀回會同步先前派送；單純呼叫派送不代表 GPU 已完成，也不是 GPU 純執行時間。

Portable limits: 32 logical inputs, 64 MiB arena, 4,194,240 generated results,
65,535 workgroups and 256 KiB shader source. Arithmetic uses float32; results may
differ slightly across adapters and from CPU double precision. GPU absence,
device loss and shader errors are reported as language errors; there is no silent
CPU fallback or promise that small arrays will run faster.

可攜上限：32 個邏輯輸入、64 MiB 緩衝區、4,194,240 個自動產生的輸出、65,535 個
工作群組及 256 KiB 著色器文字。計算使用浮點 32 位元；不同裝置及 CPU 倍精度之間
可能有微小差異。GPU 不可用、裝置遺失及著色器錯誤會回報語言錯誤，不會自動切換
CPU；小陣列的傳輸成本可能高於加速收益。

## Hosts and validation / 平台與驗證

| Target / 平台 | Backend / 後端 |
| --- | --- |
| Windows / Linux | wgpu-native (D3D12 / Vulkan) |
| Browser / 瀏覽器 | the browser's WebGPU through `web-glue/webgpu`; HTTPS or localhost required / 需 HTTPS 或 localhost |
| Android | wgpu-native over Vulkan |

Hardware availability is a deployment requirement; a portable contract does not imply that every installed
driver/browser supports WebGPU.

函式庫的 `tests/` 在各平台驗證雙語別名、產生的著色器、尺寸、範圍、所有權與快取行為
（`scripts/test-libraries.ps1 -Libraries graphics -Devices`）。部署仍需相容的硬體、驅動程式及瀏覽器。

Real browser run / 瀏覽器實測：

```text
aqua build examples/gpu_math/main.aqua -o .native-build/tests/gpu-math.wasm
node scripts/web-run.mjs .native-build/install/web .native-build/tests/gpu-math.wasm
```

`scripts/web-run.mjs` runs the application in Chromium with WebGPU (the pinned Playwright in `scripts/`) /
`scripts/web-run.mjs` 以 Chromium 的 WebGPU 執行應用程式（使用 `scripts/` 中鎖定版本的 Playwright）。
