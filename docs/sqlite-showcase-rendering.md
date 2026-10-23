# SQLite showcase clipping correction

Verified on 2026-10-23.

## Cause and runtime change

`Processing.clip(x, y, width, height)` previously stored its arguments directly as
screen coordinates. Drawing applied the current model transform, but clipping
only applied framebuffer density. The showcase correctly uses `displayScale()`
and `scale()` to draw its interface at the operating system's preferred size.
At Windows 250% scaling, its geometry grew by 2.5 while its clips did not. This
cut off the note list and excluded the feature-tour rows entirely. The browser
returns 1 for `displayScale()`, which hid the mismatch.

The desktop runtime and the JavaScript runtime shared by web and Android now
capture the model/view/projection transform when `clip()` is called. They use
the screen-aligned bounds of the transformed corners, then apply framebuffer
density once when submitting a draw. Later transforms do not move the clip;
`noClip()` removes it. No showcase or Aqua_SQLite library source change was needed.

The native GPU pixel regression and JavaScript regression both failed before
the change and pass afterward. Additional tests cover 100%, 125%, and 250%
scaling, mirrored coordinates, translation, rotation, offscreen framebuffer
density, changing the transform after defining a clip, and `noClip()`.

## Verification

| Platform | Result |
| --- | --- |
| Windows x64 | Reproduced the original screenshot at 250% display scaling. The fixed CLI and self-contained EXE render full notes and feature-tour rows; SQLite tour 16/16. |
| Ubuntu 22.04, WSL2/WSLg | Native Processing tests 50/50. Rebuilt the native graphics bridge and self-contained runtime pack. Exported and ran the real showcase ELF; both columns visible and SQLite tour 16/16. |
| Chromium/WebGPU | Wide 1440×900 layout and narrow 600×900 notes/tour tabs verified, including rendered success marks and no page errors. |
| Android API 35 x86_64 emulator | Actual APK runs with Wasmtime and Vulkan at density 420. Portrait notes/tour tabs, landscape two-column resizing, text input, adding a note, touch scrolling, and SQLite tour 16/16 checked. |
| Android API 37 preview emulator | Fixed by padding both Vulkan semaphore-value arrays when timeline metadata is present. The real APK now renders notes and the feature tour and passes SQLite 16/16. Native runtime, rendering/readback and compute smoke tests pass. See `docs/android-api37.md` for the driver mismatch and reproducible source patch. |

Windows Processing tests passed 50/50; the expanded clipping selection passed
5/5. Shared JavaScript/Android GPU tests passed 22/22, and the full Android bridge
JavaScript suite passed 98/98. Logs, before/after captures, browser results and
Android screenshots are retained in `.web-build/sqlite-ui/`.

## Generated artifacts

The corrected compiler is published at `dist/aqua/aqua.exe`. Older compiler
releases on PATH retain their bundled runtime and must not be used to rebuild
the fixed EXE. For example, from the showcase directory:

```powershell
.\build.ps1 -Aqua C:/OfficialProjects/AquariusLangTW/dist/aqua/aqua.exe -Targets windows
```

The showcase's generated files were saved at:

* `C:/OfficialProjects/Aqua_SQLite_Showcase/dist/windows/Aqua_SQLite_Showcase.exe`
* `C:/OfficialProjects/Aqua_SQLite_Showcase/dist/linux/aqua_sqlite_showcase`
* `C:/OfficialProjects/Aqua_SQLite_Showcase/dist/android/Aqua_SQLite_Showcase.apk`
* `C:/OfficialProjects/Aqua_SQLite_Showcase/dist/android-project-clipping-fixed/`
* `C:/OfficialProjects/Aqua_SQLite_Showcase/dist/android-project-api37-fixed/`

The new Android project directories preserve existing nonempty generated
projects. The APK and compiler now also include the API 37 Vulkan submission
correction. The verified web export is `.web-build/sqlite-ui/web/`.

When rebuilding the language from source, republish the Windows/Linux apphost
packs after changing desktop runtime code; executable exports embed those packs.
