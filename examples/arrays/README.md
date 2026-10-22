# 陣列 / Arrays

See the [bilingual array API](../../docs/arrays.md) for mutation, capacity, shallow
copy and half-open range semantics. Both examples produce the same values.

```sh
aqua build traditional_chinese.aqua -o arrays-zh.wasm
aqua run arrays-zh.wasm
aqua build english.aqua -o arrays-en.wasm
aqua run arrays-en.wasm
```

兩份範例使用相同核心 Wasm 實作；繁體中文範例使用中文語法與函式名稱，英文範例使用英文語法與函式名稱。
AquariusLang 也允許在同一份 `.aqua` 檔案中混用兩種語法。
