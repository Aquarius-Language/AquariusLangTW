# Previous-project compatibility review — 2026-10-27

All 34 requested projects were reviewed against the locally built current AquariusLang SDK. Obsolete native-library ABIs, JavaScript library implementations, outdated imports/dependencies, and active build/export scripts were migrated where found. Projects already using the current structure were retained. Pure Aquarius libraries may still use manifest version 1, which the current CLI supports.

The available Windows, Linux, browser, and Android test suites described below pass after the fixes. This does **not** establish full validation on every advertised OS or every physical device: macOS has no runtime pack in this checkout, Android ARM64 was cross-built rather than executed, and the hardware/service limitations below remain.

## Follow-up: packaged Unicode entry names

The reported failure in `Aqua_OpenCV_Showcase\dist\windows\showcase.exe` exposed a gap in the earlier review: its launcher smoke checks ran the Wasm/project rather than the final executable. The compiler writes JSON Unicode escapes in executable entry metadata, but the shared desktop launcher copied that JSON string without decoding it. Consequently it looked for a literal filename such as `\u4E3B\u7A0B\u5F0F.aqua`.

The correction is confined to `runtime/platform/desktop/main.c`: decode the bounded metadata string, including UTF-16 surrogate pairs, and reject invalid/missing entry strings before running the application. The executable format, ABI, language syntax and public APIs are unchanged. Both full desktop runtime packs and the published Windows/Linux templates were updated.

- Real executable regressions cover a Chinese default entry, a nested Chinese/emoji selected entry, raw UTF-8/escaped-slash metadata, and malformed metadata. The two Unicode regressions reproduce the failure with the original template. The complete executable target suite passes **21/21 on Windows and 21/21 on Linux** with the corrected launcher.
- The exact reported EXE was rebuilt and launched from its own directory with `AQUARIUS_GRAPHICS_FRAMES=1`: **exit 0**. OpenCV's `-Smoke` now launches that exported EXE and runs the 45-feature and bilingual suites; `-Run` also launches the exported EXE.
- All 11 identified affected Windows exports across nine projects were rebuilt and launched: OpenCV (both output locations), Dreamer, Painter, Snake, AudioExamples, MediaPipeExamples, NetworkLab (backend and frontend), OutOfThisWorldUI and Kanban. GUI checks use a bounded frame limit and isolated storage. The packaged NetworkLab backend additionally passed its HTTP health check and was stopped afterward.
- Evidence: `.aqua/project-review/unicode-overlay-original-template.log`, `unicode-overlay-windows-fixed.log`, `unicode-overlay-linux-fixed.log`, `unicode-opencv-exact-executable.log`, `unicode-opencv-own-smoke.log`, `unicode-reexports-results.json`. Original templates/executables/scripts remain in the `backups/unicode-*` folders.

## Tested compiler and environments

- Compiler: the repository's `dist/aqua/aqua.exe`, built from base revision `253e3181eb46a3229d572286ae9dd4464c908068` plus the uncommitted review changes. Executable SHA-256: `0080470A879A2C6E0906E5D92F5E702A9AD9B12E9FD127378ED43B378EDF8439`.
- The existing PATH release location now resolves through a task-created junction to `dist/aqua`; `aqua --help` and real project builds use the current executable. No registry PATH change was made.
- Native toolchains: pinned LLVM Clang 23.1.3, MinGW on Windows, Linux Clang/libc++, pinned Android NDK, and WASI. No authored library was converted to MSVC or a JavaScript implementation.
- Runtime packs rebuilt: Windows x64, Linux x64, web, Android x86_64, Android arm64-v8a.
- Execution: Windows x64, Ubuntu 22.04 through WSL, installed Chromium/Chrome with WebGPU/WebGL, and the isolated API 37 x86_64 `AquariusReview` emulator. Filament's browser renderer was Intel Iris Xe through ANGLE/D3D11.
- APK export used the installed pinned JDK via `JAVA_HOME` set to the repository's `.native-build/toolchains/jdk-21.0.12.1+1-windows-x64`. Bottle tests used isolated caches; changed local dependencies were explicitly updated offline. Global Bottle snapshots were not edited.

## Project results

“Pass” means the listed automated scope executed successfully. “Startup” is a bounded GUI launch, not a full interactive acceptance test. Browser means an actual headless browser executing native WebAssembly. Android execution means x86_64 on the emulator. A dash means the reviewed project does not maintain that target.

| Project | Windows | Linux | Browser | Android | Change and validation scope |
| --- | --- | --- | --- | --- | --- |
| Aqua_CRDT | Pass | Pass | Pass | Pass | Current pure Aquarius structure; WebSocket 2 dependency, corrected version metadata. Convergence on all four targets; native synchronization tests. |
| Aqua_Filament | Pass | Pass | Pass | — | Native SDK 2 library, pinned Filament C++ source, assets, private C++ runtime, thin WebGL API glue. Smoke tests and rendering checks; Linux context lifetime and browser shader/readback compatibility fixed. |
| Aqua_JBridging | Pass | — | — | — | Already current native SDK. JNI value/Unicode/NUL, exceptions/recovery, fields, and relative JDK loading scenarios. |
| Aqua_JBridging_Showcase | Pass | — | — | — | Already current SDK; FastCSV read/write round trips, portable JDK, retained-object cleanup and JVM shutdown. |
| Aqua_JoltPhysics | Pass | Pass | Pass | Pass | Native SDK 2 migration retaining pinned Jolt 5.3.0 and the same C++ engine. Smoke plus 23 validation checks; all five native build targets. |
| Aqua_LLM_API | Pass | Pass | Pass | Pass | Current pure Aquarius library; 96 local mock-server assertions per execution target. Test runners now use private working/process directories and explicit Bottle updates. |
| Aqua_OpenCV | Pass | Pass | Pass | Pass | Native SDK 2, pinned OpenCV 4.12, same C++ algorithms, private exception runtime. Algorithm, bilingual API, invalid-argument, ownership and recovery suites; all five builds. |
| Aqua_OpenCV_Showcase | Pass | Pass | Pass | Pass | Dependency/import migration; 45 functions and bilingual checks. GUI launches. Own launcher now exports complete SDK sites, `.exe` and `.apk`; committed `web-zh` regenerated and tested. |
| Aqua_PyBridging | Pass | Pass | — | — | Already current native SDK. CPython bridge scenarios and public SDK integration; scientific-package/callback/large-transfer coverage in the existing suites. |
| Aqua_PyBridging_Showcase | Pass | Pass | — | — | Current SDK; corrected Linux self-test runner. Five rendered chart types, five datasets, and background generation of 200,000 rows. |
| Aqua_SQLite | Pass | Pass | Pass | Pass | Native SDK 2 with pinned SQLite 3.54.1; prepared statements, transactions, blob/FTS/error suites, including a 1.5 MB blob. All five builds. |
| Aqua_SQLite_Showcase | Pass | Pass | Pass | Pass | Named native import and dependency migration; feature tours and bounded GUI startup. Build wrapper emits current APK format and accepts executable or compiler DLL. |
| Aqua_WebRTC | Pass | Pass | Pass | Pass | Native SDK 2, pinned mbedTLS; protocol and queues remain in C. Thin shared RTCPeerConnection/DataChannel glue. Unicode/NUL, 128 KB binary, negotiated channel ID, mesh/reconnect tests. |
| Aqua_WebSocket | Pass | Pass | Pass | Pass | Native SDK 2; retired JavaScript implementation. Text/binary/protocol and close-reason contracts, including native close code 4000. All five builds. |
| AquariusDreamer | Startup; layout tests pass | Startup | — | — | Current application structure. Markdown/layout suite: 289 assertions. macOS remains advertised by this project but cannot be executed with this SDK checkout. No external model-provider calls. |
| AquariusLang_DynamicLinkingShowcase | Pass | Pass | Pass | Pass | Replaced old buffer ABI/JavaScript library with SDK C17 library 2. `DYNAMIC_LINKING_OK`; all five library builds. |
| AquariusLang_MarbleRun_3D | Pass | Pass | Pass | Pass | Jolt 2 named import/dependency; physics and GPU integration validation. |
| AquariusLang_Painter | Pass | Pass | Pass | Pass | Current exports and HTML-only web packer. Fixed shortcut modifier matching using native event timestamps. New shared native suite: 31 pixel/history/layer/serialization checks on all four targets; browser real stroke/undo/redo/resize checks. Full app startup on Windows/Linux/Android. |
| AquariusLang_PBR_ModelViewer | Pass | Pass | Pass | Pass | Current packaging; model/PBR integration (960 triangles, two materials) and GPU checks. Updated own Android launcher produces APK. |
| AquariusLang_Snake | Pass | Pass | Pass | Pass | 236 rules checks, GUI startup/browser checks; current SDK web and Android exports, obsolete runtime rewrite removed. |
| AquariusLangTW_Advanced_PBR_Example | Pass | Pass | Pass | Pass | GPU validation of 33,543 triangles, HDR/sRGB/cube/mip/normal textures; own Android launcher now emits APK. |
| AquariusLangTW_AudioExamples | Pass | Pass | Pass | Pass | Audio validation and browser startup; current Android export. Physical microphone/speaker behavior remains unverified. |
| AquariusLangTW_BottleShowcase | Pass | Pass | Pass | Pass | SDK 2 native greeting plus transitive numbers library, updated dependencies and launcher. `BOTTLE_SHOWCASE_OK`; five native builds. |
| AquariusLangTW_CRDT_WebRTC_Showcase | Pass | Pass | Pass | Pass | CRDT/WebRTC dependency and named-import migration. Three-client convergence, direct peer transport, cursors/effects; existing user change to `scripts/common.ps1` preserved. |
| AquariusLangTW_Filament_Showcase | Pass | Pass | Pass | — | All five scenes and PNG captures. Browser checked five nonblank 960×540 captures, zero page errors/context losses, actual Intel renderer; current browser verifier and server layout. |
| AquariusLangTW_IJ_ColorInspector3D | Pass | — | — | — | Java 2 import/path migration and current launcher. ImageJ/JNI validation plus 36-stage automated GUI exercise, including bounded preview of large TIFF imports. |
| AquariusLangTW_MediaPipeExamples | Pass | Pass | Pass | Pass | CPU pose/face validation, GPU where available; current APK/web launcher and native SDK CI structure. Live physical camera unverified. |
| AquariusLangTW_MineClone | Pass | Pass | Pass | — | Added current project manifest and corrected build/test launchers/docs. Full automated survival sequence: 441 terrain chunks, gather/craft/furnace/chest/night/health, save and inventory comparison. |
| AquariusLangTW_NetworkLab | Pass | Pass | Pass | Pass | Current activity/CLI helpers and independent protocol fixtures. Native HTTP/TCP/UDP/WebSocket; browser HTTP/cookies/WebSocket/live panel; Android HTTP/TCP/UDP/WebSocket/live panel. |
| AquariusLangTW_ONNX_Showcase | Pass | Pass | Pass | Pass | All four examples: tensor, classifier, face mesh and pose. CPU/Auto verified; WebGPU on native desktop, conditional browser GPU skip. Current browser/Android scripts, opt-in CUDA testing, and isolated portable CPU executable check. |
| AquariusLangTW_OutOfThisWorldUI | Startup | — | Startup | — | Current maintained Windows/web targets; bounded native launch and real browser canvas. Existing untracked `networking.md` preserved. |
| AquariusLangTW_PatchCore | Pass | Pass | Pass | Pass | 18 core checks and nearest-neighbor GPU validation. ONNX GPU checked on Windows/Linux; current browser/Android provider limitations retained explicitly. |
| AquariusLangTW_SingleUserKanban | Pass | Pass | Pass | Pass | Removed retired C# GUI runner/core patches. Native GUI suite: 21 assertions, storage/lifecycle/attachments/recovery and 4,000 cards. Browser desktop + compact layouts; Android compact menu exercised. Existing 70 data assertions also pass. |
| AquariusLangTW_Sponza_Atrium | Pass | Pass | Pass | Pass | GPU validation: 262,267 triangles, 25 materials. Current nested SDK export, complete versioned directory, APK launcher. Cache regression passes while previous app/manifest/Wasm/runtime remain cached at the origin. |

## AquariusLang changes and regression results

The language fixes follow `CLAUDE.md`: C# stays in compiler/tooling/tests, one shared C runtime remains, SDK libraries keep one native source tree, and JavaScript glue exposes Web APIs. No language syntax feature or public SDK function-table version was added.

1. Normalize SDK environment paths with CMake's path conversion so Windows backslashes do not break native builds.
2. Link Linux external C++ libraries with both private libc++ and libc++abi, preserving exception/RTTI support and hiding dependency symbols.
3. Correct package-root asset/import resolution and the working directory of nested web modules.
4. Yield the native main thread's guest instruction budget during `Thread.Sleep`, allowing posted network completions to run.
5. Preserve UTF-8 identifier bytes in WGSL uniform reflection; the shader compiler remains responsible for Unicode validation.
6. Complete shared WebSocket argument/result translation (protocols, page location, close reason), and add shared thin WebRTC/WebGL API glue required by the migrated native libraries. Protocols, Filament rendering, shader adaptation, queues, bulk GL object operations and capability decisions remain in native code.

Regression results for the published build:

- Compiler/tooling: **679 passed, 0 failed, 1 platform skip**. The skipped Linux C++ exception/RTTI regression was executed separately in WSL: **1 passed**.
- Default native libraries across Windows/Linux/browser: **105 passed, 0 failed, 54 target-specific skips**.
- WebGL regression additionally executed in Chromium: pixel readback, PBOs, signed pixel data, extension translation and object lifetime.
- Runtime packs and the CLI were rebuilt and published locally after the fixes. No commit, push, deployment or remote CI run was made.

## Evidence, preservation and remaining limits

Raw logs, isolated project copies/caches and SHA-verified original-file backups are retained under the repository's `.aqua/project-review/`. This directory is ignored by Git; preserve it when retaining the audit evidence. Selected final logs:

- `compiler-tests-published-final.log`, `linux-sdk-exceptions-final2.log`, `default-library-tests-final.log`, `webgl-final-test.log`, `publish-final.log`.
- `filament-published-browser-final.log`, `filament-final-linux-tests4.log`, `filament-root-smoke-web-root-final.log` and the `filament-published-captures` directory.
- `opencv-showcase-browser-root-final.log`, `opencv-deployment-current-final.log`, `opencv-showcase-functions-android-final.log`, `opencv-showcase-bilingual-android-final.log`, `opencv-final-linux-validation.log`.
- `websocket-final-run.log`, `webrtc-root-contract-browser-final.log`, `webrtc-root-contract-linux-final.log`, `collab-final-run.log`, `collab-android-root-final.log`, `network-extra-final5.log`, `llm-runner-windows-final.log`, `llm-runner-linux-final.log`.
- `python-linux-final2.log`; bridge showcase logs also remain in their own `dist` directories.
- `painter-native-windows-final2.log`, `painter-native-linux-final.log`, `painter-native-android-current-final.log`, `painter-browser-current-final2.log`, `painter-gui-android-current-final.log`.
- `final-gui-platform-tests2.log`, `kanban-native-verification-final4.log`, `kanban-native-android-current-final.log`, `onnx-web-current-final2.log`, `onnx-desktop-current-final.log`, `onnx-portability-current-final.log`, `sponza-cache-current-final3.log`.
- `source-audit-completion.log`, `inventory.json`, `git-status-completion.json`, `powershell-syntax-completion.json`, `completion-audit.log` describe the final 34-project inventory/source checks. Earlier files named `cases-windows.json` and `cases-linux.json` are initial failure inventories, superseded by the final logs rather than rewritten as successes.

Original source/build/test files were backed up before replacement or retirement. No unrelated checkout was reset and no global Bottle cache was rewritten. SQLite showcase tours incremented their existing demo launch counters during earlier testing; user notes were not overwritten. Task-owned servers, reverse mappings, review apps and the isolated emulator were cleaned up; the user's original Android AVD was not used.

Remaining verification limits:

- **macOS:** no current runtime pack/host exists here; in particular AquariusDreamer's advertised macOS target was not run. This is an outstanding platform validation requirement, not a pass.
- **Android ARM64:** runtime/library cross-builds succeeded; no ARM64 device was available for execution. Emulator results cannot establish ARM64-device behavior.
- **Hardware/services:** no physical camera, microphone, speakers, pen/IME-driver matrix, NVIDIA CUDA runtime/GPU, internet peer/NAT traversal service or paid LLM provider was exercised. Local fixtures, sample media, CPU/Auto providers and available GPU backends were used. Browser/Android forced ONNX WebGPU paths unavailable in the current pack were explicitly skipped; native desktop GPU tests passed.
- **Interactive acceptance:** automated native GUI validation is recorded above. General manual native computer interaction was unavailable because the computer-use app approval timed out. Full subjective/manual acceptance of every GUI is therefore outstanding. Painter's older 25-case JavaScript-interpreter suite was replaced by the documented native/public-browser scope; old IME/download/performance reports are historical, not current passes.
- **Remote CI:** edited Pages workflows were checked locally only. They pin a real native-SDK base revision; update that pin after these local language fixes are published before treating remote CI as verified.

`Aqua_LLM_API`, `Aqua_SQLite`, `Aqua_SQLite_Showcase` and `AquariusLangTW_ONNX_Showcase` are not Git checkouts on this machine. Their edits and backups exist on disk, but Git cannot track their changes here.
