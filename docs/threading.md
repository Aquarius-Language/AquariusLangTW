# Thread / 執行緒

AquariusLang provides the same function-based threading library on desktop,
websites and Android. Use `Thread` / `執行緒` directly, or `import("Thread")` /
`匯入("執行緒")`. Both languages address the same builtin operations.

```aqua
變數 平方 = 函式(數值) { 回傳 數值 * 數值; };
變數 工作 = 執行緒.啟動(平方, [12]);
執行緒.等待(工作);
變數 答案 = 執行緒.結果(工作);  # 144
執行緒.關閉(工作);
印出(答案);
```

| English | 繁體中文 | Behavior / 行為 |
| --- | --- | --- |
| `Thread.Start(function, arguments = [])` | `執行緒.啟動(函式, 引數陣列 = [])` | Start an isolated worker; returns an opaque handle. / 啟動獨立工作，回傳控制代碼。 |
| `Thread.Wait(handle, timeout = -1)` | `執行緒.等待(工作, 逾時 = -1)` | True when stopped, false on timeout; never consumes the result. / 停止時回傳真，逾時回傳假，保留結果。 |
| `Thread.Result(handle)` | `執行緒.結果(工作)` | Wait and return a fresh copy of the result; propagate worker errors. / 等待並取得結果副本；工作錯誤會傳給呼叫者。 |
| `Thread.Cancel(handle)` | `執行緒.取消(工作)` | Request cancellation without waiting; true only for the first request to a running worker. / 要求取消但不等待；僅首次對執行中的工作要求取消時回傳真。 |
| `Thread.Close(handle, timeout = -1)` | `執行緒.關閉(工作, 逾時 = -1)` | Cancel, join, release resources; true on success, false on timeout. Repeated close returns true. / 取消、等待停止並釋放資源；逾時回傳假，可再次關閉。 |
| `Thread.IsDone(handle)` | `執行緒.是否完成(工作)` | Whether execution stopped, including failure or cancellation. / 是否已停止，包含錯誤及取消。 |
| `Thread.Sleep(milliseconds)` | `執行緒.休眠(毫秒)` | Sleep; inside a worker cancellation wakes it and returns false. / 休眠；工作取消時喚醒並回傳假。 |
| `Thread.IsCancellationRequested()` | `執行緒.是否要求取消()` | Cancellation flag in a worker; false on the main execution. / 工作的取消旗標；主程式固定回傳假。 |

Timeouts use integer milliseconds: `0` polls and `-1` waits indefinitely.
Sleep accepts nonnegative integer milliseconds. The maximum is 2,147,483,647.
Wait includes failure/cancellation completion; use Result to obtain the value or
report the failure. Closing intentionally discards the result: obtain it first
when needed. After a successful Close, only repeated Close is valid.

逾時使用整數毫秒：`0` 立即檢查，`-1` 無限等待。休眠必須是非負整數，
最大為 2,147,483,647。等待完成包含工作失敗或取消；使用「結果」讀取值或錯誤。
關閉會捨棄結果，需要時請先取得結果。成功關閉後，只能再次關閉。

Workers receive a snapshot of their function, lexical captures and arguments at
Start. Function arguments may themselves contain functions/closures. Changes to
arrays, hashes or captured variables remain inside that worker; parent changes
after Start cannot race with worker reads. Results support numbers (preserving
integer/float/double), booleans, strings, null/void, arrays and hashes, including
cycles and shared references. Each Result call creates a fresh graph. Results
cannot contain functions, modules or host handles. Packets are bounded to 16 MiB,
4,096 collection objects and 128 nesting levels.

工作在啟動時複製函式、捕獲變數與引數；引數中也可以傳入其他函式或閉包。
陣列、雜湊表及捕獲變數的修改不會跨工作共用。結果支援數值型別、布林、字串、
空值、陣列及雜湊表，保留循環與共用參照；每次讀取取得獨立副本。
結果不可包含函式、模組或主機控制代碼。

Worker code can use compiled language operations, the core Array / 陣列 module,
`len/長度`, `last/最後一個`, `rest/其餘`, `push/加入`, `floodFill/區域填色` and
Thread.Sleep / IsCancellationRequested. Perform imports and capture portable
script functions before Start. UI, network, file, GPU and other host capabilities
are confined to the parent; using them in workers reports an error. Workers
cannot start or wait for other workers. This prevents self-join and cyclic waits.

工作可以執行語言運算、陣列函式庫、上述可攜內建函式、休眠及取消檢查。
請在啟動前匯入腳本並捕獲需要的可攜函式。介面、網路、檔案、GPU 等主機能力
留在主程式；工作中使用會回報錯誤。工作不可啟動或等待其他工作，因此不會互相
等待造成死結。

Cancellation is cooperative at compiled scheduler checkpoints, including pure
loops and interruptible sleeps. A cancellation request can race with normal
completion. Code may observe the flag and return in its current slice, but
cleanup code in a worker is not guaranteed to run: keep resource ownership in
the parent. Close never kills a thread while its store is executing. A timed-out
Close retains the handle for a later retry. Runtime disposal cancels all workers
before joining them. Close completed workers to release their slots (64 open
handles per session). A worker starts with a copy of the current guest heap, so
prefer a few coarse CPU jobs over a thread per array element.

取消會在編譯後的排程檢查點生效，純迴圈也能停止，休眠可以被喚醒。
取消與正常完成可能同時發生；工作內的收尾程式不保證執行，因此資源請由主程式
持有。關閉會等待引擎安全退出；逾時保留控制代碼。執行階段釋放時，會先取消
所有工作再逐一等待。每個工作階段最多保留 64 個未關閉工作，完成後請關閉。

| Target | Implementation |
| --- | --- |
| Windows, Linux, Android and standalone exports | `runtime/host/threads.c`: an OS thread and a separate Wasmtime store per worker, cancellation signals and interruptible sleep. |
| Browser / website export | `runtime/host/threads_web.c` with `web-glue/thread`: module Web Workers with separate WebAssembly instances. Waits yield the main event loop. No SharedArrayBuffer or cross-origin isolation headers are required. Serve exported files over HTTP(S); worker scripts must be allowed by CSP. |

Inside a worker the guest runtime itself resolves the portable builtins and refuses host access, so a host's worker
implements only Sleep and IsCancellationRequested. See [examples](../examples/threading/README.md). Native stores
follow Wasmtime's [multithreaded embedding model](https://docs.wasmtime.dev/examples-multithreaded-embedding.html);
browser workers use [standard Worker messaging](https://developer.mozilla.org/docs/Web/API/Web_Workers_API/Using_web_workers).

Verification:

```powershell
./scripts/build-wasm-runtime.ps1 -Check
dotnet test compiler/AquariusTests -m:1 --filter FullyQualifiedName~AquariusTests.Threading
aqua build examples/threading/verification.aqua -o .native-build/tests/threading.wasm
node scripts/web-run.mjs .native-build/install/web .native-build/tests/threading.wasm
```
