# 陣列 / Arrays

See the [bilingual array API](../../docs/arrays.md) for mutation, capacity, shallow
copy and half-open range semantics. Both examples produce the same values.

```sh
aqua build traditional_chinese.aqua -o arrays-zh.wasm
aqua run arrays-zh.wasm
aqua build english.aqua -o arrays-en.wasm
aqua run arrays-en.wasm
```

兩份範例使用相同核心 Wasm 實作；英文範例採用英文函式名稱與 Aquarius 的繁體中文語法。
