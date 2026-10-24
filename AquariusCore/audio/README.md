# 跨平台音訊

`Audio`／`音訊` 使用 **miniaudio 0.11.25**。解碼、重採樣、混音、循環、聲像與音高全部由同一份 `audio.wasm` 執行：桌面使用 Wasmtime，瀏覽器即時播放使用 AudioWorklet 中的 WebAssembly。核心只含介面、語言註冊、資源所有權和程式庫 ABI，不參考 Wasmtime、V8、作業系統或音訊裝置套件。

格式固定為 **48,000 Hz、雙聲道、交錯排列的 float32 PCM**。輸出會限制於 −1..1。相同輸入和操作順序的離線 PCM 可逐位元比較；實際喇叭、驅動、系統音量及硬體重採樣仍由輸出設備決定。

桌面原生橋接 `aquarius_audio` 僅使用 miniaudio 的裝置和單生產者／單消費者環形緩衝區；即時裝置回呼不執行 .NET、解碼、配置記憶體或取得受控鎖。瀏覽器使用 AudioWorklet 的訊息埠傳送控制命令，由音訊執行緒依序執行與混音，不使用 `decodeAudioData`、瀏覽器振盪器或瀏覽器混音效果。

## 使用

```aqua
變數 音訊 = 匯入("音訊");
變數 裝置 = 音訊.建立裝置();
變數 提示音 = 裝置.建立音調(440, 0.5d);
提示音.設定音量(0.6d);
裝置.啟動();
提示音.播放();
# 保持應用程式的事件迴圈運作，結束時再釋放。
```

| 物件 | 繁體中文介面／英文介面 |
| --- | --- |
| 音訊 | `建立裝置/CreateDevice()`、`建立離線裝置/CreateOfflineDevice()`；`取樣率/SampleRate`、`聲道數/Channels` |
| 裝置 | `啟動/Start()`、`暫停輸出/Suspend()`、`建立音調/CreateTone(頻率,秒數)`、`建立樣本/CreateBuffer(樣本陣列)`、`載入/Load(資源路徑)`、`產生樣本/Render(影格數)`、`釋放/Dispose()` |
| 聲音 | `播放/Play()`、`暫停/Pause()`、`停止/Stop()`、`設定音量/SetVolume(0..1)`、`設定聲像/SetPan(-1..1)`、`設定音高/SetPitch(0.25..4)`、`設定循環/SetLooping(布林值)`、`跳至/Seek(秒數)`、`是否播放中/IsPlaying()`、`取得時長/GetDuration()`、`取得位置/GetPosition()`、`釋放/Dispose()` |

`暫停`保留位置，`停止`回到起點，已播放完畢的聲音再次`播放`會從頭開始。音調提供 5 ms 的淡入／淡出，避免端點爆音。同一裝置可同時混合 64 個聲音。音調範圍 20..20000 Hz、0.001..60 秒；載入 WAV、FLAC 或 MP3，支援最長 180 秒、編碼檔 32 MiB、每個裝置已解碼樣本總量 128 MiB。資源應在播放前載入；目前採完整解碼，尚無串流音檔、錄音或三維空間音訊。

離線裝置不開啟硬體，每次`產生樣本`接受 1..4096 個影格，回傳左右聲道交錯的陣列。即時裝置不能呼叫此函式，離線裝置不能`啟動`。匯入、查詢函式目錄和執行未使用音訊的程式都不開啟音訊裝置。裝置釋放時釋放所有聲音，執行環境結束時釋放所有裝置；重複釋放不產生副作用，已釋放的物件不能再次使用。

瀏覽器即時輸出需要 HTTPS 或 localhost 和 AudioWorklet。若瀏覽器限制自動播放，`啟動`會顯示「開啟音訊」按鈕；按下後繼續執行。取消執行會移除此按鈕並關閉裝置。

## 建置與驗證

來源固定於 `native/vendor/miniaudio`，保留上游雙授權，SHA-256 由建置腳本檢查。不增加 NuGet 或 npm 套件。

```powershell
./scripts/build-audio-runtime.ps1
./scripts/build-audio-runtime.ps1 -Check
./native/build.ps1
```

使用固定版本的 LLVM/Clang 23.1.3 與 wasi-sdk 33 的 WASI sysroot（版本與 SHA-256 見 `scripts/wasm-toolchain.json`）；首次可先執行 `scripts/install-wasm-toolchain.ps1`，`scripts/build-audio-runtime.ps1 -Check` 會確認已提交的二進位檔與原始碼一致。Wasm 使用 WASI libc 的配置與數學實作，但不匯入 WASI：未使用的 miniaudio 檔案 VFS 被封閉，只能從主機傳入的記憶體解碼。離線測試驗證 Wasmtime／Node 樣本逐位元一致，並覆蓋靜音、混音、控制、WAV 解碼、限制、全繁體中文語言介面與資源釋放；單元測試不需要音訊硬體。

`AquariusWebCompiler/tests/audio.test.mjs` 也驗證 FLAC／MP3 解碼與重採樣，以及 128／256／4096 影格區塊的樣本一致性。`tests/audio-browser.mjs` 需先建置音訊示範網站；以 `AQUARIUS_AUDIO_WEB` 指定網站目錄。它使用 Chrome 的靜音輸出端驗證真正的即時 AudioWorklet，不需要測試機的喇叭。
