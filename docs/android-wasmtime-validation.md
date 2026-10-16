# Android Wasmtime validation — 2026-10-17

Android now executes Aquarius and bundled library WebAssembly through Wasmtime 48.0.2. QuickJS-NG 0.17.0 runs the shared JavaScript capability libraries, with native Java services and the existing wgpu/Vulkan bridge. Javet, V8, their notices, and the separately packaged V8 ICU data were removed. Android ICU supplies grapheme and word segmentation.

`prepare-android.ps1` builds both ARM64 and x86-64. Runtime source archives are pinned by SHA-256; Wasmtime uses its upstream Cargo lockfile. The generated APK includes `libwasmtime.so` and `libaquarius_runtime.so` for both ABIs. The new libraries' ELF load segments have 16 KB alignment. APK inspection found no Javet/V8/ICU payload.

## Unit tests

| Suite | Result |
|---|---:|
| Full .NET suite | 1,596 passed; 35 optional desktop tests skipped |
| Web/shared host tests | 312 passed |
| Android host bridge tests | 95 passed |
| Android Java camera and texture tests | 13 passed |
| Language server and VS Code tests | 24 passed |
| Native GPU binary batch parser | Passed (`ANDROID_GPU_BATCH_PARSER_OK`) |

Logs are under `.android-build/wasmtime-*-tests*.log`, with Android bridge output also in the smoke log. The .NET TRX is `AquariusTests/TestResults/wasmtime-migration.trx`.

## Emulator validation

The regression contract executes through the packaged Android runtime, rather than a mocked WebAssembly engine. It checks typed-array offsets, validation, import/export metadata, custom sections, callback reentry, BigInt results, table identity and updates, globals, memory growth and immediate invalidation of old views, trap recovery, host exceptions, and canonical ES-module identity. `scripts/test-android.ps1` requires both the Wasmtime engine marker and successful execution.

All 23 standard emulator cases passed: 16 runtime/example/device cases and the seven original applications. These include Processing and Processing/WGPU, native GPU drawing/readback and compute, image/document round trips, audio output, ONNX CPU inference, Jolt, modules and paths, front/back cameras, touch/pointer events, IME input, keys, and Back cancellation. Logs are in `.android-build/wasmtime-emulator-final.log` and `.android-build/smoke/*.log`.

The original applications were also opened in portrait and landscape. This exposed Android's fullscreen extracted IME replacing Painter's custom canvas in landscape. `KeyboardView` now requests `IME_FLAG_NO_EXTRACT_UI` and `IME_FLAG_NO_FULLSCREEN`. The input contract and seven application APKs are rebuilt and checked again after that fix; their results are in `.android-build/wasmtime-ime-and-projects-final.log`.

Both real MediaPipe CPU inference entries also passed (`姿態模型推論驗證通過` and `臉部模型推論驗證通過`), bringing the total to 25 distinct emulator execution cases. Their APKs and logs are `mediapipe-pose.*` and `mediapipe-face.*` under `.android-build/smoke`.

Interactive checks confirmed MarbleRun's touch joystick starts simulation, Snake starts and responds to direction input, Painter draws a stroke, Kanban changes columns after dismissing its tour, PBR responds to tap-to-pause, and Audio responds to piano/melody controls. Painter's landscape IME capture now retains the canvas; the input-method diagnostic reports `mFullscreenMode=false`. Evidence is in `.android-build/wasmtime-interactions.log` and `.android-build/wasmtime-visual/`.

| Original application | APK under `.android-build/smoke/` |
|---|---|
| MarbleRun 3D | `AquariusLang_MarbleRun_3D.apk` |
| Snake | `AquariusLang_Snake.apk` |
| Painter | `AquariusLang_Painter.apk` |
| Kanban | `AquariusLangTW_SingleUserKanban.apk` |
| MediaPipe | `AquariusLangTW_MediaPipeExamples.apk` |
| PBR model viewer | `AquariusLang_PBR_ModelViewer.apk` |
| Audio | `AquariusLangTW_AudioExamples.apk` |

Screenshots and runtime logs are retained in `.android-build/wasmtime-visual/`.
The machine-readable APK/log manifest is `.android-build/wasmtime-validation-results.json`. All 25 APKs were checked for both Wasmtime ABIs and absence of Javet/V8/ICU payloads; all seven project APKs pass `zipalign -c -P 16 4`.

## Reproduction

```powershell
rustup target add aarch64-linux-android x86_64-linux-android
./scripts/prepare-android.ps1 -Ndk C:/Android/Sdk/ndk/28.2.13676358
dotnet test AquariusTests -m:1
npm test --prefix AquariusWebCompiler
node --test AquariusAndroid/tests/*.test.mjs
./scripts/test-android.ps1 -Emulator -DeviceServices -Adb C:/Android/Sdk/platform-tools/adb.exe
```

Use `-ProjectRoots` for sibling projects, `-CaseNames` for selected cases, and `-SkipBuild` when the current compiler has already been built. APKs and per-case logs are retained in `.android-build/smoke`.

The device baseline is the isolated x86-64 API 35 emulator with Vulkan. Both ABIs are built and packaged; physical ARM64 execution remains unverified. These are correctness and interaction checks, with no new phone performance claim.
