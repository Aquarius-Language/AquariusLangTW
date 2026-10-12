# 星泉 PBR 材質與模型工作室

同一份 AquariusLang 原始碼與 Wasm，在 Windows 和 WebGPU 瀏覽器展示即時 PBR：
上排陶瓷、下排金屬，粗糙度由左至右增加；右侧為匯入的 OBJ／MTL 雙材質雕塑。
空白鍵暫停／繼續旋轉，視窗可縮放。

需要包含 Models／PBR 核心函式庫的新版 AquariusLang 編譯器與桌面執行階段。

```powershell
./建置.ps1 -編譯器 C:/path/to/aqua.exe
aqua run pbr_models.wasm
aqua run pbr_models.wasm --entry smoke.aqua
aqua run pbr_models.wasm --entry gpu-smoke.aqua

# 產生獨立 EXE，需編譯器隨附最新 Windows runtime pack
./建置.ps1 -編譯器 C:/path/to/aqua.exe -桌面
./dist/pbr_models.exe

# 預覽網站
python -m http.server 8080 --directory web
```

瀏覽器開啟 localhost:8080。`web/` 已包含編譯後程式、資產與主機，直接部署此資料夾。
`.github/workflows/pages.yml` 沿用其他星泉專案的 Pages 流程：main 的 web 更新或手動觸發時，
上傳 web 並部署。建立 GitHub repository 後，將 Settings → Pages → Source 設為 GitHub Actions。

核心支援 OBJ 多邊形、法線、UV、負索引、凹面三角化、MTL 金屬度／粗糙度。
第一版為不透明、無貼圖材質與方向光；不含 glTF、動畫、環境貼圖或陰影。
模型載入後在 setup 建立保留網格；每幀只更新材質、旋轉與繪製。

`smoke.aqua` 驗證 960 三角形與兩個材質群；`gpu-smoke.aqua` 使用真實像素檢查法線與粗糙度。
完整核心 API 與驗證流程見 AquariusLangTW 的 docs/pbr-models.md。

assets/orbit.obj、orbit.mtl 為此專案原創幾何與材質，依 CC0 提供；程式採 MIT，見 LICENSE。
