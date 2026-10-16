# Android graphics improvements — 2026-10-14

These measurements used the earlier Javet/V8 runtime. Android now uses Wasmtime with a QuickJS capability host; the figures below have not been remeasured for that engine migration.

The Android renderer now records ordered binary drawing batches, reuses GPU upload storage, retains sphere meshes, and configures the presentation surface only when needed. The Aquarius drawing API, Wasm engine, and native wgpu/Vulkan backend remain in place.

On the same Samsung SM-A5360 running Android 16, Snake improved from a fresh **1.49 FPS** baseline to **37.88 FPS** after warmup. This is approximately **25.4× higher presentation throughput**. These numbers describe Snake's initial screen on this phone; other applications still need their own performance measurements.

**Measured comparison**

| Metric | Original renderer | Updated renderer |
|---|---:|---:|
| Recording duration | Approximately 10 seconds | Approximately 30 seconds |
| Presentation intervals measured | 11 | 937 |
| Presentation cadence | 1.49 FPS | 37.88 FPS |
| Mean interval | 670.77 ms | 26.40 ms |
| Median interval | 669.80 ms | 25.32 ms |
| Nearest-rank p95 interval | 730.66 ms | 32.24 ms |
| Vulkan queue submissions | 6,181 / 12 presentations | 1,876 / 938 presentations |
| Approximate submissions per presentation | 515 | 2 |
| Steady-state swapchain creations | 12 | 0 |
| Mean `dequeueBuffer` slice | 2.43 ms | 0.026 ms |

The final recording contains 24.74 seconds between its first and last presentation, followed by about five seconds without new presentations. The cadence calculation uses the 937 consecutive presentation intervals. The original recording includes partial frames at its boundaries, so the submission ratio is approximate. FPS is calculated from consecutive `QueuePresentKHR` timestamps rather than Android View/HWUI statistics. These are presentation-call intervals, not input-to-display latency or GPU execution times. Perfetto recording itself can affect timing.

The earlier [diagnostics](android-graphics-diagnostics.md) measured 1.40 FPS in a separate recording. The fresh 1.49 FPS baseline above provides the comparison for this implementation.

**Implemented changes**

- [gpu-commands.mjs](../AquariusAndroid/template/app/src/main/assets/aquarius/gpu-commands.mjs) records clears, dynamic draws, and immutable mesh draws in a bounded, little-endian binary stream. Compatible adjacent complete primitives merge while retaining their vertex order and uniform snapshots. Clear, target, texture, clip, blend, and uniform changes retain their ordering boundaries.
- [android-gpu.mjs](../AquariusAndroid/template/app/src/main/assets/aquarius/android-gpu.mjs) sends the stream when a flush is required. Texture writes, resource disposal, offscreen sampling, readback, compute, polling, and presentation flush earlier drawing. Resource descriptors still use small JSON messages; GPU uploads no longer use base64 strings.
- [NativeBridge](../AquariusAndroid/template/app/src/main/java/org/aquariuslang/android/NativeBridge.java) passes typed-array views and their actual byte offset/length to JNI. The native call consumes the view synchronously. Pending GPU work retains wgpu-owned uploaded data, never a JavaScript heap pointer.
- [gpu.cpp](../AquariusAndroid/template/app/src/main/cpp/gpu.cpp) uses three reusable upload-storage slots with aligned dynamic uniform offsets and bounded bind-group caches. Each binary batch uses one command encoder and retains render passes across compatible draws. Targets keep their attachment views. Full pixel uploads initialize render targets so the first later draw preserves those pixels.
- Surface configuration is cached by surface generation and extent, with recovery for outdated/lost/suboptimal acquisitions. Rotation and surface replacement invalidate the cache; ordinary presentation does not recreate the swapchain.
- [processing.mjs](../AquariusCore/hosting/processing.mjs) combines stroke segments into one mesh. Android caches sphere geometry by radius, detail, and fill color, with a bounded cache and eviction flushes. Model/view/projection and lighting uniforms remain per draw. Arbitrary mutable retained shapes continue through the dynamic path.
- Custom shaders referencing `vertex_index` or `primitive_index` retain separate draw boundaries. Both Android and browser Processing preserve the original segment draws for those shaders.
- [FrameClock](../AquariusAndroid/template/app/src/main/java/org/aquariuslang/android/FrameClock.java) waits for an eligible Choreographer callback. Frame deadlines subtract work already performed instead of adding a full frame period after rendering. Lifecycle suspension and cancellation remain supported.

**Local source benchmark**

These are warmed-up Windows/Node measurements of shared geometry and Android packing, with the native boundary replaced by a counting stub. They exclude Aquarius/Wasm callback execution, Java/JNI, Vulkan, GPU work, and display scheduling. Three warmups and the median of nine samples were used. Historical timings come from the original evidence snapshot.

| Synthetic redraw | Original host JS | Updated host JS | Updated binary transfer |
|---|---:|---:|---:|
| 100 filled rectangles | 2.96 ms | 0.88 ms | 30.3 KiB |
| 100 outlined rectangles | 14.45 ms | 1.57 ms | 142.8 KiB |
| 20 outlined rounded rectangles | 23.12 ms | 1.42 ms | 300.3 KiB |
| 20 outlined ellipses | 38.96 ms | 2.53 ms | 536.6 KiB |
| 12 spheres, detail 28 × 18 | 48.58 ms | 0.034 ms | 26.0 KiB |

Each updated synthetic redraw produces one native batch/submission before presentation and no GPU-payload base64 characters. Sphere timing excludes the initial mesh construction/upload, which occurs during warmup. It is not a claim that creating a new sphere costs 0.036 ms. The same-position synthetic spheres do not represent a complete PBR application.

**Verification**

The implementation is covered by JavaScript ordering/ownership tests, native binary-parser tests, Java unit tests, Android packaging tests, and a physical-device graphics regression. The device regression renders three frames and checks offscreen dependencies, pixel updates, clipping, depth/sphere drawing, and GPU readback. It also checks that newly uploaded pixels survive a target's first render pass. Rotation and background/resume were exercised, with the original rotation setting restored. The Snake screen was visually inspected after those transitions.

The first full .NET run exposed two existing example-mirror mismatches for Processing showcase and Processing/wgpu. The desktop copies were synchronized with their existing responsive examples; the following full run passed 1,393 tests with 30 optional desktop GPU tests skipped.

Both ARM64 and x86_64 JNI libraries compile with the existing pinned NDK and `-O2`. Physical execution in this pass covers ARM64. The Java camera/module tests passed 14 cases. All 248 JavaScript tests passed, including the new binary transport, ordering, mesh, deadline, and custom-shader regression checks. The final Android/export checks passed 49 cases, and the completed device graphics regression passed all three frames.

**Using the updated target**

Rebuild the compiler after preparing the native libraries, then regenerate Android projects/APKs from the existing `.wasm` applications. Previously generated projects contain their own runtime snapshot and do not update automatically.

```powershell
./scripts/prepare-android.ps1 -Ndk <path-to-NDK-28.2.13676358>
dotnet build AquariusCli -m:1
node --test AquariusAndroid/tests/*.test.mjs
./scripts/test-android.ps1 -BuildApks -Emulator -CaseNames graphics,triangle,compute,processing-wgpu -Adb <adb-path>
```

The `graphics` case is [graphics.aqua](../examples/android_smoke/graphics.aqua). The `-Emulator` runner flag also works with an explicitly selected connected physical device. Device-service tests that grant camera permission are separate from this graphics pass.

Use the [benchmark](../scripts/diagnose-android-graphics.mjs) and [trace analyzer](../scripts/summarize-android-graphics-trace.mjs) to reproduce performance measurements. The [evidence snapshot](android-graphics-improvements.json) preserves source hashes, benchmark output, and trace summaries. Larger local artifacts are retained under `.android-build/graphics-improvements`.

The separately identified test APK is built at `.android-build/graphics-improvements/snake-project/app/build/outputs/apk/debug/app-debug.apk`, using `org.aquariuslang.perf.snake`. Existing installed Snake/Painter packages were used as baselines and were not replaced.

**Remaining limits**

Snake still spends much of its frame budget in Aquarius/Wasm/JS runtime work. The change removes the measured renderer overhead but does not guarantee 60 FPS. Mutable retained shapes remain dynamic, readback still uses the existing byte-array/base64 return path, and camera/ONNX services retain their own transport and inference costs. Those paths require separate profiling before further changes.
