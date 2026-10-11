# 原創解碼測試音色

`tone.flac` 與 `tone.mp3` 取自星泉聲音工坊自製提示音的前 0.15 秒，依專案 MIT 授權使用。
FLAC 為 22,050 Hz 單聲道；MP3 為 44,100 Hz 雙聲道，以實際壓縮格式驗證解碼及跨取樣率的重採樣。
使用 FFmpeg 轉檔產生測試資源；執行測試、星泉建置或播放不需要 FFmpeg。

```powershell
ffmpeg -i 星泉提示.wav -t 0.15 -ar 22050 -ac 1 tone.flac
ffmpeg -i 星泉提示.wav -t 0.15 -ar 44100 -ac 2 tone.mp3
```
