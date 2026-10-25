# 攝影機範例

所有註解、變數、提示文字及新增攝影機介面均使用繁體中文。既有的繪圖模組名稱沿用專案原有名稱。

| 腳本 | 用途 | 是否啟用攝影機 |
| --- | --- | --- |
| `冒煙測試.aqua` | 載入模組並查詢宿主支援，適合不具備攝影機的自動測試。 | 否 |
| `裝置清單.aqua` | 顯示目前可列舉的裝置名稱及識別碼。 | 否 |
| `影格驗證.aqua` | 擷取兩張影格，驗證尺寸、像素數量、不透明度、序號及關閉狀態。 | 是 |
| `main.aqua` | 即時繪圖展示；關閉視窗後釋放裝置。 | 是 |

## 桌面執行

請先安裝具備裝置擷取支援的 FFmpeg，確認執行檔位於搜尋路徑，或設定 `AQUARIUS_FFMPEG`。開啟實體裝置前，請確認作業系統已允許桌面應用程式使用攝影機。

```powershell
aqua run examples/webcam/冒煙測試.aqua
aqua run examples/webcam/裝置清單.aqua
aqua run examples/webcam/影格驗證.aqua
aqua run examples/webcam/main.aqua
```

影格驗證會立即關閉裝置，即時展示則持續擷取到視窗關閉。錯誤訊息會說明不支援、找不到裝置、權限、模式不支援或逾時等問題。

即時展示讀取最新完整影格；繪圖較慢時會略過舊影格，序號可能跳號。暫停或慢速更新後不會重播累積畫面。

## 網頁執行

```powershell
$來源 = @(Get-ChildItem examples/webcam -Filter '*.aqua' | ForEach-Object FullName)
dotnet run --project AquariusCli -- build @來源 --root examples/webcam --entry main.aqua --target web -o .web-build/webcam-demo
```

將輸出目錄以本機靜態伺服器或 HTTPS 提供，並開啟頁面。瀏覽器可能要求攝影機權限。網頁預設執行 `main.aqua`；使用測試入口可分別執行其他腳本。瀏覽器在取得權限前可能不提供完整裝置名稱或識別碼。

## 自動驗證

```powershell
./scripts/test-webcam.ps1
```

預設使用 FFmpeg 測試影像與 Chrome 虛擬攝影機，不開啟實體裝置。另加 `-Hardware` 才執行實體裝置驗證。完整契約、部署依賴與清理行為見 [攝影機架構指南](../../docs/webcam.md)。
