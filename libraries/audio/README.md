# Audio｜音訊

Tones, WAV/FLAC/MP3 sounds and a 64-voice stereo mixer for AquariusLang, with realtime devices that play to the
speakers and offline devices that render PCM for the script. One C implementation on every target, built on
[miniaudio 0.11.25](third_party/miniaudio/README.md).

```aqua
變數 音訊 = 匯入("音訊");
變數 裝置 = 音訊.建立裝置();
變數 提示音 = 裝置.建立音調(440, 0.5d);
提示音.設定音量(0.6d);
裝置.啟動();
提示音.播放();
```

## Module

| Object | English / 繁體中文 |
| --- | --- |
| `Audio｜音訊` | `CreateDevice｜建立裝置()`, `CreateOfflineDevice｜建立離線裝置()`; constants `SampleRate｜取樣率` (48000), `Channels｜聲道數` (2) |
| `Audio.Device` | `Start｜啟動()`, `Suspend｜暫停輸出()`, `CreateTone｜建立音調(Hz, seconds)`, `CreateBuffer｜建立樣本(samples)`, `Load｜載入(path)`, `Render｜產生樣本(frames)`, `Dispose｜釋放()` |
| `Audio.Sound` | `Play｜播放()`, `Pause｜暫停()`, `Stop｜停止()`, `SetVolume｜設定音量(0..1)`, `SetPan｜設定聲像(-1..1)`, `SetPitch｜設定音高(0.25..4)`, `SetLooping｜設定循環(boolean)`, `Seek｜跳至(seconds)`, `IsPlaying｜是否播放中()`, `GetDuration｜取得時長()`, `GetPosition｜取得位置()`, `Dispose｜釋放()` |

- The format is fixed: 48 kHz, stereo, interleaved float PCM, clamped to -1..1.
- `Pause` keeps the position, `Stop` returns to the start, and `Play` on a sound that played to its end starts over.
  Tones are 20..20000 Hz, 0.001..60 s, amplitude 0.25 with 5 ms fades. `CreateBuffer` takes 2..17,280,000
  interleaved stereo values in -1..1. `Load` decodes WAV, FLAC or MP3 of 1..32 MiB completely into memory; sounds are
  at most 180 s and a device holds at most 128 MiB of decoded samples and 64 sounds.
- `Load` reads a packaged asset (`aqua build ... --assets dir`), by its package path or by a path under the
  application directory as `目前工作目錄 + "/音色/提示.wav"` builds it; on Windows, Linux and Android it otherwise reads
  that file from disk. The web reads packaged assets only.
- Offline devices never open hardware: `Render` takes 1..4096 frames and returns 2 × frames interleaved values.
  `Start` fails on an offline device and `Render` on a realtime one. Importing the module or creating a device opens no
  hardware; a realtime device opens its output at the first `Start`.
- `Dispose` releases a device with all its sounds (or one sound); repeating it does nothing, and every other call on a
  disposed object fails. The end of the application disposes every device.
- Errors stop the script with `Audio.<Function>: <message>`, e.g. `Audio.SetVolume: Expected a finite number in 0..1.`

## How it works

| | Windows | Linux | Android | Web |
| --- | --- | --- | --- | --- |
| Decoding, tones, mixing | miniaudio (`src/engine.c`) | same | same | same, in the `audio.wasm` side module |
| Output | WASAPI | PulseAudio, ALSA | AAudio, OpenSL ES | `web-glue/audio`: AudioContext + AudioWorklet |

- `src/audio.c` is the language surface (names, checks, messages, object lifetime); `src/engine.c` the only file that
  compiles miniaudio, with every symbol private; `src/output_web.c` the web output.
- Native output: miniaudio's device callback copies frames from a lock-free ring buffer; a pump thread (pthreads)
  keeps about 1024 frames (21 ms) rendered ahead in 256-frame blocks. Calls from the script and the pump take turns on
  the device's engine lock; the callback never waits and the runtime's thread never blocks on audio.
- Web output: the mixer renders 512-frame blocks on the main thread and the glue posts them to a minimal AudioWorklet
  processor, which plays them and reports each finished block; those reports pace the mixer, which stays 3072 frames
  (64 ms) ahead. No decoding, mixing or resampling runs in JavaScript, and no timer keeps the application alive. If the
  browser's autoplay policy holds the AudioContext suspended, the glue resumes it at the next pointer, key or touch
  gesture on the page. Realtime output needs a secure context (HTTPS or localhost).
- miniaudio's SIMD paths are disabled and floating-point contraction is off for `engine.c`, so every target renders
  bit-identical PCM; the tests print exact checksums, which also equal the previous implementation's `audio.wasm`.

## Building and testing

The library builds as a default library inside the runtime (`-DAQUA_LIBRARIES=audio`) or alone as an external one
(`cmake -S libraries/audio ...` gives `aqua_audio.dll` / `libaqua_audio.so`, or `audio.wasm` on the web). Native
targets link pthreads (static winpthreads on Windows); miniaudio loads the platform's audio libraries at run time.

`tests/` holds `<case>.aqua` and the exact expected output. Compile each with the sounds packaged:
`aqua build tests/<case>.aqua --root tests --assets tests/sounds -o <case>.wasm`.

| Test | Checks |
| --- | --- |
| `offline` | silence, tone, pause/stop/seek/looping, gain and pan, the old parity mix, block-size independence |
| `decode` | packaged WAV, FLAC (22.05 kHz mono) and MP3 (44.1 kHz stereo) decoding and resampling |
| `traditional` | the whole Traditional Chinese interface |
| `voice_limit`, `disposed` | the 64-sound limit and disposed objects; both end with the expected error (exit code 1) |
| `playback` | needs an audio output device: starts the output, plays, stops, suspends and restarts |

`tests/sounds/tone.flac` and `tone.mp3` come from the previous implementation's tests (cut from the project's own
prompt sound, MIT); `quarter.wav` is 480 frames of a constant 0.25.

## Dependencies

- miniaudio 0.11.25, vendored unmodified in `third_party/miniaudio` (Public Domain or MIT No Attribution), SHA-256
  checked at configure time.

## Differences from the previous implementation

- Devices and sounds are native objects: `typeOf` gives `audio.device` / `audio.sound` instead of `module`.
- Wrong argument types and counts fail with the runtime's signature messages (e.g. `Audio.Device.CreateBuffer: argument
  1 must be an array, got integer.`) instead of the C# wording; range, limit and state errors keep the old messages.
  Engine failures that were `miniaudio operation failed (-3)` now say what failed (64-sound limit, size limits,
  undecodable data).
- Web: no "開啟音訊" button. `Start` returns at once and, when the browser blocks autoplay, sound begins at the first
  user gesture on the page. The mixer moved from the AudioWorklet to the main thread (the worklet only plays blocks),
  so a main thread busy for more than about 64 ms causes a gap.
- Sounds the script no longer reaches are released once they finish playing, and unreachable devices once they have no
  sounds and are not started (previously both lived until `Dispose` or the end of the application).
- Android: output now goes through miniaudio (AAudio/OpenSL ES) in native code instead of Java's AudioTrack; the
  previous Java backend paused output while the activity was paused, which needs an activity lifecycle hook the SDK
  does not offer yet.
