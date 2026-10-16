# Threading / 多執行緒範例

- `parallel.aqua`: two CPU jobs with a function parameter and captured multiplier;
  English library names, expected result `1001000`.
- `繁體中文.aqua`: 繁體中文函式庫、傳入函式參數、兩個工作，預期結果 `91`。
- `cancellation.aqua`: 立即檢查、取消、逾時等待及安全關閉，預期結果 `真`。
- `verification.aqua`: 共用平台驗證，檢查函式參數、快照隔離、循環結果、逾時、取消、休眠與純迴圈的安全關閉；印出 `THREADING_OK`。

```powershell
aqua build examples/threading/parallel.aqua -o parallel.wasm
aqua run parallel.wasm
aqua build examples/threading/繁體中文.aqua -o chinese.wasm
aqua run chinese.wasm
aqua build examples/threading/cancellation.aqua --target web -o dist/threading-web
aqua build examples/threading/繁體中文.aqua --target android -o dist/threading-android
```

Full bilingual API, isolation and lifecycle rules: [Thread / 執行緒](../../docs/threading.md).
