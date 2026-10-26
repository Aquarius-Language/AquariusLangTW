# Android API 37 Vulkan compatibility

The Android native runtime keeps Vulkan rendering, presentation, compute and
readback on API 37. Applications use the same Aquarius source as on other
supported platforms.

## Cause

The API 37 x86_64 GFXStream driver crashed inside
`gfxstream::vk::ResourceTracker::on_vkQueueSubmit`. Disassembly of the installed
`vulkan.ranchu.so` showed it reading an element through a null
`VkTimelineSemaphoreSubmitInfo::pWaitSemaphoreValues` pointer while pruning a
binary wait semaphore.

wgpu's submission included a timeline signal and a binary-only wait list. Its
timeline-value array for the wait side was empty. Vulkan ignores values for
binary semaphores; the guest driver's pruning code nevertheless accessed that
array. This affected a second queue submission, including presentation and
readback. Switching the emulator's host GPU renderer did not avoid the guest
driver bug.

Current [upstream Mesa GFXStream source](https://chromium.googlesource.com/external/gitlab.freedesktop.org/mesa/mesa/+/HEAD/src/gfxstream/guest/vulkan_enc/ResourceTracker.cpp)
checks the value pointer and count before accessing it. Aquarius also normalizes
submissions for the older guest driver shipped in the installed API 37 image.

## Source correction

`native/patches/wgpu-hal-29.0.3-timeline-values.patch` pads both wait and signal
value arrays whenever either side uses timeline metadata. Existing timeline
values are retained. Dummy values for binary semaphores are ignored by Vulkan.
Submissions containing only binary semaphores still omit the timeline extension.

`scripts/prepare-android-wgpu.ps1` verifies the pinned wgpu-native 29.0.1.1 and
wgpu-hal 29.0.3 source archives, applies the patch, runs native submission tests,
and builds both Android ABIs using the upstream Cargo lockfile. The standard
`scripts/prepare-android.ps1` workflow calls it, so rebuilding cannot silently
replace the correction with the original prebuilt library. Published compiler
packages embed the resulting libraries; application authors need no Rust or NDK.

The focused tests in `native/tests/vulkan_semaphore_values.rs` verify binary
waits with timeline signals, timeline waits with binary signals, preservation
of real timeline values, and binary-only submissions. The mixed cases fail
against the original builder and pass with the patch.

For a source build, install Rust Android targets, NDK r28c and libclang, then run:

```powershell
./scripts/prepare-android-wgpu.ps1 -Ndk C:/Android/Sdk/ndk/28.2.13676358
dotnet build AquariusCli -c Release -m:1
./scripts/test-android.ps1 -Configuration Release -SkipBuild -Emulator -CaseNames runtime,graphics,compute
```

The full native preparation command remains `scripts/prepare-android.ps1`.
`-TestOnly` on the WebGPU preparation script runs the native submission tests
without building Android libraries.

Verification logs and SQLite showcase screenshots are retained under
`.web-build/sqlite-ui/api37-*`. ARM64 is built from the same corrected source;
device execution is checked on the installed x86_64 emulator.

Verified on 2026-10-23 using the API 37 Google Play x86_64 image and Android
Emulator 37.2.12.0 with SwiftShader. The SQLite showcase renders its notes and
feature tour and completes all 16 SQLite checks. The standard Android smoke
runner passes `runtime`, `graphics` and `compute`: Wasmtime contract checks,
three rendering/readback frames with offscreen targets and clipping, and Vulkan
storage-buffer computation returning `[2, 4, 6, 8]`. The four native semaphore
tests and all 98 Android JavaScript bridge tests also pass.

The same patched rendering/readback and compute APKs also pass on API 35.
Both ARM64 and x86_64 native library hashes were checked inside the final APK
against the libraries embedded in the updated compiler.
