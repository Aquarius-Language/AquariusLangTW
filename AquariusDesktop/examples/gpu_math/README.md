# GPU Mathematics / GPU 數學

Compile and run this example with a WebGPU adapter / 以可用的 WebGPU 裝置執行：

```text
aqua build examples/gpu_math/main.aqua -o gpu-math.wasm
aqua run gpu-math.wasm
```

Expected / 預期：`[[5, 9, 15], [8, 12, 18], [1, 2, 3]]`.

The second dispatch uploads only its changed input. The first input remains on
the GPU. Both dispatches use the cached pipeline / 第二次派送只上傳變更的輸入，
第一組輸入仍保留於 GPU，兩次派送共用管線快取。

See [the bilingual rule API](../../../docs/gpu-math.md) / 參閱[雙語規則介面](../../../docs/gpu-math.md)。
