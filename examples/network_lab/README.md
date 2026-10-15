# 星泉網路實驗室

`frontend/aqua.toml` 與 `backend/aqua.toml` 是兩個獨立的星泉專案。
所有 `.aqua` 都使用繁體中文變數、函式與函式庫介面；協定名稱和 HTTP 標頭保留標準寫法。

```powershell
./Build.ps1
./Launch.ps1 -Platform web
./Launch.ps1 -Platform desktop
./Launch.ps1 -Platform android -Device emulator-5554
./Launch.ps1 -Verify
```

`Build.ps1 -Platforms web,desktop` 可只建置指定平台。桌面封裝產生 Windows x64 EXE；
同一份 Wasm 亦可在 Linux 桌面主機執行。Android APK 建置需要 JDK、Android SDK 及 Gradle；
啟動時以 `adb reverse` 把裝置的 8787 連接埠連回本機，適用模擬器與 USB 實機。
有多個裝置時請明確指定 `-Device`。

後端只監聽 `127.0.0.1:8787`。網頁由 Aquarius 後端的 `靜態檔案` 功能直接提供，
不需要另外啟動 Python／Node 伺服器。關閉桌面前端或在啟動腳本按 Enter 即停止該腳本啟動的後端。
使用前請先停止占用 8787 的其他程式。`dist/server.log` 及 `server-error.log` 保存後端紀錄。

前端每秒 POST 一筆繁體中文 JSON，顯示傳送內容、收到的內容、HTTP 狀態、
成功／失敗次數、Cookie 工作階段計數和往返延遲圖。連線失敗會顯示錯誤，下一秒再試。
`驗證.aqua` 另外檢查中文資料、序號、Cookie 延續及 HTTP 422 錯誤回應。

這個範例使用輪詢 HTTP 展示每次請求／回應；網路函式庫另提供 WebSocket、SSE、
原始 TCP／TLS 和 UDP。一般瀏覽器不開放原始 TCP／UDP，請先用 `網路.支援(...)` 查詢。
`協定驗證.aqua` 是開發用的 DNS／TCP／UDP／WebSocket 檢查；執行前需在編譯器原始碼目錄
啟動 `node AquariusWebCompiler/tests/network-echo.mjs`，並啟動本專案後端。
範例的工作階段存於記憶體，重啟後清除；正式部署請設定 HTTPS 憑證及明確的來源政策。
