# 跨平台 WebGPU 紋理與 HDR

紋理的尺寸、格式、mip、綁定與生命週期驗證都在 graphics 函式庫的 C 程式碼中，所有平台相同。Windows、Linux 與 Android 透過 `webgpu.h` 使用 wgpu-native，網頁使用 `web-glue/webgpu` 實作的同一份 `webgpu.h`（瀏覽器的 WebGPU）。封裝資產由執行環境直接讀取位元組，不經過 Java 或 JavaScript。

`WGPU.Device.LoadTexture(path, width, height, format, dimension, mipLevels)` 與 Processing 畫布的 `loadTexture` 使用封裝資產路徑。格式為 `r8unorm`、`rgba8unorm`、`rgba8unorm-srgb`、`rgba16float`，維度為 `2d` 或 `cube`。Cube 必須是正方形，面順序為 +X、-X、+Y、-Y、+Z、-Z。資料為小端序、mip 優先、面／層其次，每層緊密排列；每個 mip 的尺寸為 `max(1, size >> level)`。

可直接提供原始位元組，或使用 gzip 內容編碼。解壓縮長度、CRC、mip 數、半浮點的有限值與 128 MiB 資源上限會在 GPU 上傳前驗證。一般 GPU buffer 與 Android 繪圖批次仍保留 64 MiB 上限。PNG、FBX、HDR 的匯入或離線烘焙不是這個低階 API 的責任。

WGSL 的 group 0 保留 Processing ABI，group 1 保留 UserUniforms。Group 2 可使用 0..7 的具名 `texture_2d<f32>` / `texture_cube<f32>`，以及 binding 8 的 filtering sampler。紋理可在頂點或片段階段使用，sampler 使用 clamp-to-edge 與線性 min/mag/mip 過濾。

```aquarius
變數 shader = canvas.createShader(vertexSource, fragmentSource);
變數 probe = canvas.loadTexture("assets/probe.gz", 256, 256, "rgba16float", "cube", 9);
shader.setTexture("probe", probe);
```

```wgsl
@group(2) @binding(0) var probe: texture_cube<f32>;
@group(2) @binding(8) var probeSampler: sampler;
// textureSampleLevel(probe, probeSampler, direction, roughnessMip)
```

名稱、維度、device 所有權與已釋放資源都會檢查。Windows 與網頁在每次 draw 建立對應的綁定快照；Android 設定紋理前提交既有批次，保留設定與繪製順序。缺少綁定會明確失敗。

`Processing.createGraphics(width, height, "P3D", "rgba16float")` 可建立線性 HDR 畫布。先用 HDR 畫布繪製光照，再使用一般 `rgba8unorm` 畫面與自訂著色器執行 tone mapping。既有三參數呼叫保留原行為。

`WGPU.Device.CreateRenderTarget(width, height, "rgba16float")` 同樣支援 HDR，`target.GetTexture()` 可將附件交給下一個 shader。取得的紋理與 target 共用生命週期；釋放其中任一方會釋放 target。讀取像素與儲存圖片使用 RGBA8；HDR 必須先轉入 RGBA8 target。跨 target 採樣會提交來源的先前繪製，禁止同一附件一邊寫入一邊採樣。

這些能力是一般圖學功能。Cerberus、原例的三盞光源、投影數值、GGX 烘焙、BRDF 與場景控制保留在 `AquariusLangTW_PBR` 的腳本與資源中，沒有加入 core 的特殊分支。

設計依循 [WebGPU 紋理格式與綁定契約](https://www.w3.org/TR/webgpu/)及 [WGSL 紋理介面](https://www.w3.org/TR/WGSL/)。驗證包含 descriptor / WGSL layout、真實 GPU 的 cube mip / HDR / target dependency / dispose，以及 Android 批次與瀏覽器場景測試。

## 多階段 GPU 運算的繪圖目標 / Render targets for multi-pass GPU work

模擬、程序化生成與路徑追蹤這類多階段運算需要的功能（napali 的 WebGPU 移植即以此實作）：

- 格式：`CreateRenderTarget(w, h, format)` 接受 `rgba8unorm`、`rgba16float`、`r16float`、`r32float`、`rgba32float`。32 位元浮點（`r32float`、`rgba32float`）在許多 GPU（iOS、多數 Android、部分瀏覽器）上無法過濾，因此所有平台一律視為不可過濾：只用 `textureLoad`（與 `textureDimensions` 等尺寸查詢）讀取的紋理會宣告為不可過濾，任何格式都能綁定；以取樣器取樣、`GenerateMipmaps` 或直接呈現 32 位元浮點目標會回報明確錯誤，需要過濾時請改用 `rgba16float`。`blend: "replace"` 不設混合狀態，因此不可混合的格式也能寫入。
- 選項：`CreateRenderTarget(w, h, format, {"attachments": 2, "mipLevels": 9})`。片段著色器輸出 `@location(0..n-1)` 寫入各附件，`GetTexture(i)` 取得第 i 個；`GenerateMipmaps()` 以 2x2 盒式濾波由 mip 0 產生其餘層級。
- 群組 2：紋理可放在 0..7 與 18..31；binding 8 為線性 clamp 取樣器，宣告 binding 16 的 `sampler` 則得到重複定址、mipmap 線性、8 倍非等向的取樣器。
- `UserUniforms` 上限 4096 位元組，另支援 `array<vec4<f32>, N>`（`Set` 傳 4N 個值）與 `mat3x3<f32>`（`Set` 傳 9 個行優先值）。
- `DrawProcedural(shader, count, first, [x, y, w, h])` 只繪製剪裁矩形內的像素；`Flush()` 立即送出目前的繪製。長時間的全螢幕通道可分帶繪製並逐帶送出，避免 GPU 看門狗重設裝置。
- `Prepare(shader)` 預先編譯此目標繪製該 shader 所用的管線；網頁上非同步編譯，不會阻塞瀏覽器的 GPU 執行緒（大型 shader 在 Chrome/D3D12 可能需要一分鐘以上）。

English summary: render targets take `r16float`, `r32float` and `rgba32float` (32-bit float is unfilterable on every
target: read it with `textureLoad`; sampling, `GenerateMipmaps` or presenting it fails clearly, use `rgba16float` to filter) besides `rgba8unorm`/`rgba16float`; `{"attachments": n, "mipLevels": m}` gives several outputs
(`GetTexture(i)`) and mip chains (`GenerateMipmaps()`); group 2 has textures at 0..7 and 18..31 and an optional
repeating anisotropic sampler at binding 16; `UserUniforms` (4096 bytes) adds `array<vec4<f32>, N>` and
`mat3x3<f32>`; `DrawProcedural` takes an optional scissor and `Flush()` submits, so long passes can be drawn in bands;
`Prepare(shader)` compiles a pipeline ahead of the first draw, asynchronously on the web. Test: `tests/wgpu_targets.aqua`.

## 運算著色器與體積紋理 / Compute shaders and volumes

`device.CreateCompute(wgsl)` 編譯含 `@compute fn cs_main` 的運算著色器，回傳的 Shader 與繪圖著色器一樣使用 `Set`、`SetInt`、`SetTexture`、`SetBuffer`（群組 1 的 UserUniforms 與群組 2 的綁定），另有 `SetStorage(name, texture)` 綁定儲存紋理，`Dispatch(x, y = 1, z = 1)` 以工作群組數執行。運算著色器的群組 2 另外可宣告：

- `var<storage, read_write>` 儲存緩衝區（綁定 9..15；繪圖著色器仍只能唯讀）；
- `texture_storage_2d<格式, write | read_write>` 與 `texture_storage_3d<...>`，格式為 `rgba8unorm`、`rgba16float`、`r32float`、`rgba32float`（WebGPU 核心允許作為儲存紋理的格式），綁定在紋理位置 0..7 與 18..31；2D 儲存紋理是繪圖目標的 `GetTexture(i)`（單一 mip、上述格式），3D 則是體積；
- `texture_3d<f32>`（繪圖著色器也可用）。

`device.CreateVolume(w, h, d, 格式 = "rgba16float")` 建立 3D 紋理（初始為 0），運算著色器以儲存紋理寫入，任何著色器以 `texture_3d<f32>` 取樣或 `textureLoad`。每次 `Dispatch` 先送出寫入或讀取目標上已記錄的繪製，之後記錄的繪製與運算都會看到它的結果。群組 0 對運算著色器是空的。

English summary: `device.CreateCompute(source)` compiles a compute shader (`cs_main`); it takes `Set`, `SetInt`,
`SetTexture` and `SetBuffer` like render shaders, `SetStorage(name, texture)` for storage textures, and runs with
`Dispatch(x, y, z)` (or `DispatchIndirect(buffer, offset = 0)`: three u32 workgroup counts an earlier dispatch wrote
into a storage buffer at that offset in numbers). Compute shaders may also declare `read_write` storage buffers (9..15), storage textures
(`texture_storage_2d` / `texture_storage_3d` in rgba8unorm, rgba16float, r32float or rgba32float, `write` or `read_write`:
a render target's single-mip color texture or a volume) and `texture_3d<f32>` (render shaders too). `device.CreateVolume(w,
h, d, format)` makes a zero-filled 3D texture. Draws recorded on the targets a dispatch uses are submitted before it;
later work sees its writes. Test: `tests/wgpu_compute.aqua` (windows-x64 so far).

實例與間接繪製 / Instanced and indirect draws: `target.DrawInstanced(shader, vertexBuffer, instances, firstInstance = 0)`
draws the whole vertex buffer `instances` times; `target.DrawIndirect(shader, vertexBuffer, argumentsBuffer, offset = 0)`
draws with four u32 a compute shader wrote into a storage buffer at `offset` floats (vertex count, instance count,
first vertex, first instance), so GPU culling can decide how many instances are drawn. Vertex shaders read the instance
as a second parameter: `fn vs_main(input: VertexInput, @builtin(instance_index) instance: u32)`. Storage buffers are
created with the indirect usage for this.

## 效能分析與裝置限制 / Profiling and device limits

- `WGPU.Stats(reset = true)`: what the module did since the last reset: `submits`, `renderPasses`, `computePasses`,
  `draws`, `dispatches`, `bufferWrites` / `bufferWriteBytes`, `textureWrites` / `textureWriteBytes`, `buffersCreated` /
  `bufferBytesCreated`, `bindGroups`, `pipelines`, `readbacks`, `flushScans`, the CPU milliseconds inside the calls that
  wait or build (`submitMs`, `pipelineMs`, `readbackMs`, `acquireMs` (waiting for a swap-chain image), `presentMs`),
  `calls`: `{member: {"count", "ms"}}` with the native time spent in each module member, and `nativeMs`, their sum.
- `WGPU.NativeMs()`: milliseconds spent inside module members since start (never reset), for profilers that time
  their own scopes and want the native part of them.
- `device.Profile(true)` turns on GPU timestamps where the adapter supports them (`device.ProfileSupported()`; it
  returns false otherwise): every render and compute pass begun afterwards writes its start and end time.
  `device.ProfileScope(name)` labels the passes begun after it. `device.ProfileFrame()` ends the frame's timestamps
  (resolved into a readback buffer, nothing waits) and returns the newest frame whose results arrived, usually two to
  four frames old: `{"frame", "spanMs", "passes": [{"name", "ms", "count"}]}` with the GPU time summed per label, or
  null. `device.Profile(false)` stops.
- Devices ask the adapter for more than WebGPU's default limits where it has them: up to 16 storage buffers per shader
  stage and storage buffers up to 1 GiB. `CreateBuffer(length)` accepts what the device allows. Storage buffers bind
  at 9..15 and 32..39 of group 2. Vertex buffers can be bound with `SetBuffer` too (read as 12 floats a vertex).
- `WGPU.CreateWindow(width, height, title, {"fullscreen": true})`: the whole primary display at its own resolution; it
  stays on screen when another window takes the focus.
