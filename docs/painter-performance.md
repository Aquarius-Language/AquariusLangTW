# Large-canvas drawing and fill

The shared Wasm runtime provides `floodFill(pixels, width, height, x, y, replacement)`
and its Traditional Chinese alias `區域填色`. The first argument is a row-major
numeric array with exactly `width * height` elements. Dimensions and coordinates
must be language integers; dimensions are 1..8192, with at most 16,777,216 elements.
The seed must be in bounds and the replacement must be numeric. Invalid arguments
fail before changing the array.

The operation replaces the four-connected region whose values equal the seed's
original value, using the language's numeric equality, and returns the number of
changed entries. Replacing a region with its existing value returns zero. A NaN
seed has no equal region and returns zero. Aliases observe changes; snapshots made
with `push` keep their shared-storage copy-on-write behavior. Modified native-owned
arrays enter the existing dirty queue, so `PImage.updatePixels()` sees the changes.
The algorithm executes in the linked freestanding Wasm runtime on desktop, web and
Android. It does not use a host-JS or C# flood-fill implementation. Its span stack
grows with pending runs instead of allocating another whole-canvas image.

```text
變數 圖片 = 畫布.取得();
圖片.載入像素();
變數 數量 = 區域填色(圖片.pixels, 寬, 高, 橫, 縱, 顏色);
如果 (數量 > 0) { 圖片.更新像素(); }
```

Applications using this new builtin must be rebuilt with the updated compiler.
Older ABI 2 applications keep working with the new hosts and receive the transport,
batching and scheduling improvements without adopting the new fill call. The new
builtin is portable ID 5; IDs 1..4 retain their behavior.

The JS Wasm adapter now uses `aqua_collection_due` and
`aqua_collection_generation`, as the desktop adapter already did. Scheduler yields
do not force a full collection without allocation pressure or released host
leases. Explicit reachability checks still force a fresh trace before native
handles can be reclaimed. Old ABI 2 binaries without these exports keep eager
collection. Numeric array transport uses one memory view per buffer, preserves
numeric types, and reacquires the view when mixed or nested values grow memory.

Processing converts RGBA/ARGB directly, without allocating temporary arrays per
pixel. One-to-one canvas snapshots copy the byte buffer directly. Android GPU
readback returns a Java byte array through JNI's owned Uint8Array conversion; the
adapter exposes its underlying bytes as Uint8Array. No base64 strings are created
for GPU texture or compute-buffer readback.

`endDraw()` ends the canvas drawing scope while keeping compatible draws queued.
Texture consumers, readback, texture writes, disposal and presentation still flush
in order. Pending input gets an 8..32 ms processing budget based on queue length;
events retain their order and no movement samples are discarded. Individual long
callbacks can exceed this budget.

Regression coverage includes randomized fill versus reference traversal, holes,
unsigned ARGB, aliases, shared-storage snapshots, invalid dimensions, million-entry
arrays, stale collection generations, old ABI 2 binaries, nested transport memory
growth, binary readback offsets and float buffers, offscreen ordering and all drag
points. Painter's `測試/Android效能封裝.mjs` instruments an isolated generated APK
for real-emulator callback/presentation timings; it is excluded from delivered APKs.
