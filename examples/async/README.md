# Async / 非同步範例

- `basics.aqua`: async functions, `await`, `Promise.All`, `Race`, `AllSettled`, `New`, `Timeout`, `Then`, `Catch` and a
  recursive async function, in English.
- `繁體中文.aqua`: 以繁體中文撰寫的 `非同步 函式`、`等待`、`承諾.全部`、`競速`、`拒絕`、`捕捉`、`然後`、`最終`、`逾時` 與遞迴。
- `verification.aqua`: the shared check for every target (ordering, combinators, errors, Chinese syntax, typed bodies);
  prints `ASYNC_OK`.
- `downloads.aqua`: concurrent HTTP requests with the network library's `...Async` functions (needs network access).

```powershell
aqua run examples/async/basics.aqua
aqua run examples/async/繁體中文.aqua
aqua build examples/async/verification.aqua -o .native-build/tests/async.wasm
node scripts/web-run.mjs .native-build/install/web .native-build/tests/async.wasm          # the web
scripts/android-run.ps1 .native-build/tests/async.wasm -Runtime .native-build/install/android-x86_64 -Run
```

`basics.aqua` prints:

```text
start coffee
start tea
ordered, state: pending
ready tea
ready coffee
[coffee, tea]
start espresso
start latte
ready espresso
race: espresso
ready latte
served cake
refused: nothing ordered
fulfilled
rejected
42
逾時 / Timeout
3 2 1 liftoff
```

`繁體中文.aqua` 印出：

```text
[甲完成, 乙完成, 丙完成]
快完成
42
錯誤：不能是負數
50 [最終]
逾時 / Timeout
3 2 1 發射
```

The full syntax, the Promise / 承諾 API and the editor checks: [async functions and promises](../../docs/async.md).
