# Android validation — 2026-10-13

Historical results for the former Javet/V8 runtime. The current Android target uses Wasmtime; see [Wasmtime migration validation](android-wasmtime-validation.md).

The solution includes `AquariusAndroid`, with native Java services, V8 WebAssembly execution, and native WebGPU/Vulkan graphics and compute. CLI project generation and direct debug APK builds were both exercised. The bundled Gradle wrapper also built an APK in a directory containing spaces, with a Chinese application label.

## Results

| Check | Result |
|---|---|
| Full .NET suite | 1,393 passed; 30 optional desktop GPU tests skipped |
| Final Android, external-asset and script-runner regression checks | 63 passed |
| JavaScript host suites | 233 passed |
| Native Java camera conversion and module identity tests | 14 passed |
| Android runtime smoke cases | 14 passed |
| Final original-project APK builds and emulator startup checks | 7 passed |
| Mobile web portrait/landscape checks with touch and Unicode input | 22 passed |
| Native library/APK alignment | All 12 packaged ARM64/x86_64 native libraries and APK 16 KB alignment checked |

The Android cases cover language execution, native platform capabilities, Processing showcase, Processing / wgpu, Color mapping, multilingual input, document/image round-tripping, Jolt, GPU rendering/readback, GPU compute, module imports, script-relative paths, camera, and input.

Device-service assertions exercised front/back camera frames, owned RGBA snapshots, capture sequence/time, session disposal/reopening, touch pressure, Window pointer events, keyboard/IME input, and Back cancellation. Camera permission grant and denial were also checked separately. Native ONNX CPU inference passed the actual MediaPipe pose and face model validation scripts.

Mobile browser checks cover the seven projects below plus Color mapping, Processing showcase, multilingual input, and Processing / wgpu, at 412 × 915 and 915 × 412. Native presentation was visually checked after correcting the surface's colour conversion.

## Original project artifacts

Each original project's `建置.ps1` supports `-Android` for project generation and `-APK` for APK output. Its source changes and build results are in the original project directory.

| Project | Final APK |
|---|---|
| Marble3D | `AquariusLang_MarbleRun_3D/dist/android.apk` |
| Snake | `AquariusLang_Snake/dist/android.apk` |
| Painter | `AquariusLang_Painter/dist/android.apk` |
| Kanban | `AquariusLangTW_SingleUserKanban/dist/android.apk` |
| MediaPipe | `AquariusLangTW_MediaPipeExamples/dist/android.apk` |
| PBR model viewer | `AquariusLang_PBR_ModelViewer/dist/android.apk` |
| Audio | `AquariusLangTW_AudioExamples/dist/android.apk` |

Local reports are retained under `.android-build`: `final-tests.log`, `final-targeted-tests.log`, `node-final.log`, `final-native-smoke.log`, `final-project-apks.json`, `final-project-apks.log`, `final-alignment.log`, `default-wrapper-build.log`, and `mobile-web/results.json`. The native colour preview is `colors-native-final.png`; mobile screenshots are in `mobile-web`.

## Environment and limits

Builds used .NET 8, JDK 21, Gradle 8.13, NDK r28c, and installed SDK `android-37.0`, with application target API 36 and minimum API 29. Both ARM64 and x86_64 native libraries were built and packaged. Runtime checks used an isolated x86_64 Android API 35 emulator with Vulkan; physical ARM64 device execution remains unverified.

ONNX inference currently uses the native CPU provider. Native WebGPU supports graphics and general compute; the Android ONNX WebGPU provider correctly reports unsupported. The generated APKs use debug signing; release distribution requires the application's own signing configuration.
