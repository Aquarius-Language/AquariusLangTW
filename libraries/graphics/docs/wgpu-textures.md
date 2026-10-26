# 跨平台 WebGPU 紋理與 HDR

Core 的 `WgpuTextureDescriptor`、`IWgpuTexture` 與 `WgpuTextureLayout` 定義相同的尺寸、格式、mip、綁定與生命週期契約。Windows 使用 Silk.NET / wgpu-native，網頁使用 WebGPU，Android 使用 JNI / wgpu-native；共用 JavaScript 驗證與主機註冊位於 `AquariusCore/hosting`。

Android 封裝資產使用 JNI 的 owned typed array 傳輸，避免高解析度材質經過重複的 Java base64 字串。這也供一般 `Files`／影像資源載入共用，不需要提高應用程式的 Java heap 上限。

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
