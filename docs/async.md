# Async functions and promises / 非同步函式與承諾

AquariusLang has JavaScript-style asynchronous code on every target (Windows, Linux, Android, the web): `async` /
`非同步` functions, `await` / `等待`, and the `Promise` / `承諾` module. They run on the runtime's own task scheduler,
the one that already lets a script wait for a network reply while event handlers keep running, so nothing blocks
the program while a promise is pending.

星泉在所有平台都支援類似 JavaScript 的非同步程式：`非同步` / `async` 函式、`等待` / `await` 與 `承諾` / `Promise`
模組。它們使用執行環境本身的工作排程，承諾尚未完成時不會阻擋其他程式。

```aqua
var load = async function(name, ms) {
    await Promise.Delay(ms);          # waits without blocking anything else
    return name + " ready";
};
var both = await Promise.All([load("a", 30), load("b", 10)]);
print(both);                          # [a ready, b ready]
```

```aqua
變數 載入 = 非同步 函式(名稱, 毫秒) {
    等待 承諾.延遲(毫秒);
    回傳 名稱;
};
印出(等待 承諾.全部([載入("甲", 30), 載入("乙", 10)]));   # [甲, 乙]
```

## Syntax / 語法

| English | 繁體中文 | Meaning / 意義 |
| --- | --- | --- |
| `async function(...) { ... }` | `非同步 函式(...) { ... }` or `非同步函式(...) { ... }` | An async function: a call returns a promise at once; the body runs as its own task. / 非同步函式：呼叫立即回傳承諾，本體作為獨立工作執行。 |
| `await value` | `等待 值` | Suspend until the promise settles; give its value, or fail with its reason. A value that is not a promise is the result itself. / 暫停直到承諾完成，取得其值；被拒絕時以原因失敗。非承諾的值直接成為結果。 |

- `await` / `等待` is valid inside async functions and at the top level of the entry script. Inside a synchronous
  function it is a compile error: "await is only valid inside an async function or at the top level of a script".
  `等待` / `await` 只能用在非同步函式中或主腳本最上層；在同步函式中使用是編譯錯誤。
- The words are keywords only where they start an async function or an await, like `struct` / `結構`. Existing
  programs that use them as names keep working: `執行緒.等待(工作)`, `var 等待 = ...; 等待(1)`, `var async = 1`. A
  member name after `.` is always a name. Inside an async function `等待(x)` is always an await; outside one, `等待(...)`
  and `等待[...]` call or index a variable when the source declared one with that name.
  這些字只在開始非同步函式或等待時是關鍵字，因此現有把它們當名稱的程式照常運作。
- Write a space after `等待` and after `非同步` (`等待 取得()`); `等待取得()` is one name.
- `await` binds like a prefix operator: `await f(x).y + 1` is `(await f(x).y) + 1`; use parentheses for
  `(await f(x)).y`.
- An async function's body starts running right away, inside the call, until it first waits for a pending promise
  (or its time slice ends); then the caller continues with the promise. Returning a promise from an async function
  passes that promise's outcome on. Two names (`var 載入, load = async function ...`) and annotations work as for
  other functions; an annotated return type is the type of the value the promise gives.
- Awaiting a value that is not a pending promise continues at once (JavaScript would yield to its microtask queue
  first; the result is the same).

## Errors / 錯誤

A runtime error inside an async function rejects its promise with the error's message. `await` of a rejected promise
fails the awaiting code with that message, so an unhandled error travels up through every `await` like a
synchronous error. Handle it with `Catch`, `Then(onFulfilled, onRejected)` or `Promise.AllSettled`:

```aqua
var r = await fetchUser(7).Catch(function(message) { return {"error": message}; });
```

A rejected promise that nobody awaits or handles is reported when it is garbage-collected or when the program ends:
`未處理的承諾拒絕 / Unhandled promise rejection: <reason>`, and the exit code becomes 1.
沒有被等待或處理的拒絕會在回收或程式結束時回報，並使結束碼為 1。

Await can only suspend code that runs as a task of the runtime's loop: the entry script, async functions, event
handlers and callbacks libraries start asynchronously. Code a library runs to completion inside a call (an imported
module's top level, an `Array.Sort` comparator) cannot wait for a pending promise and fails with "this code runs
synchronously and cannot wait for a pending promise". Use an async function or `Then` there.

## Promise / 承諾

| English | 繁體中文 | Behavior / 行為 |
| --- | --- | --- |
| `Promise.New(function(resolve, reject))` | `承諾.建立(函式(解決, 拒絕))` | A promise the executor settles; an error in the executor rejects it. / 由執行函式解決或拒絕的承諾。 |
| `Promise.Resolve(value?)` | `承諾.解決(值?)` | A fulfilled promise (a promise is returned as it is). / 已完成的承諾。 |
| `Promise.Reject(reason?)` | `承諾.拒絕(原因?)` | A rejected promise. / 已拒絕的承諾。 |
| `Promise.All(array)` | `承諾.全部(陣列)` | The values in order; rejects with the first rejection. / 依序的值，任一拒絕即拒絕。 |
| `Promise.AllSettled(array)` | `承諾.全部結算(陣列)` | `[{"status": "fulfilled", "value": v} or {"status": "rejected", "reason": r}, ...]`. / 每個承諾的結果。 |
| `Promise.Race(array)` | `承諾.競速(陣列)` | Settles like the first to settle. / 與最先完成者相同。 |
| `Promise.Any(array)` | `承諾.任一(陣列)` | The first value; rejects when all reject. / 第一個完成的值，全部拒絕才拒絕。 |
| `Promise.Delay(ms, value?)` | `承諾.延遲(毫秒, 值?)` | Fulfilled with the value after `ms` milliseconds, without blocking. / 不阻擋地延遲。 |
| `Promise.Timeout(promise, ms, reason?)` | `承諾.逾時(承諾, 毫秒, 原因?)` | Settles like the promise, or rejects with `逾時 / Timeout`. / 逾時即拒絕。 |
| `Promise.IsPromise(value)` | `承諾.是承諾(值)` | Whether the value is a promise. / 是否為承諾。 |
| `p.Then(onFulfilled, onRejected?)` | `p.然後(完成時, 拒絕時?)` | A new promise of the handler's result; handlers run at a later turn. / 處理結果的新承諾。 |
| `p.Catch(onRejected)` | `p.捕捉(拒絕時)` | `Then(null, onRejected)`. |
| `p.Finally(callback)` | `p.最終(回呼)` | Runs the callback, then settles like `p`. / 執行回呼後與 p 相同。 |
| `p.State`, `p.Value`, `p.Reason`, `p.IsDone` | `p.狀態`, `p.值`, `p.原因`, `p.是否完成` | `"pending"` / `"fulfilled"` / `"rejected"`, the value, the reason, whether settled. |

Arrays may mix promises and plain values (a value counts as already fulfilled). `Promise` / `承諾` is a global, and
`import("Promise")` / `匯入("承諾")` gives the same module. `typeOf` of a promise is `"promise"`.

## Libraries / 函式庫

Library functions that wait (network requests, sockets) already let other tasks run while they wait, so calling them
from an async function makes concurrent code without callbacks. Libraries add `...Async` / `非同步...` variants (`GetAsync` / `非同步取得`) that
return a promise at once instead of waiting, for starting several operations together:

```aqua
var net = import("Network");
var pages = await Promise.All([net.GetAsync("https://example.com/a"), net.GetAsync("https://example.com/b")]);
```

A native library registers such a variant of any deferred function with `aqua_define_async`, with no other code; see
[external libraries](external-libraries.md).

## Editor checks / 編輯器檢查

Besides the compiler's error for `await` outside async functions, the language server warns about:

- a promise nobody awaits or handles (`load(1);` as a statement; attaching `Then` / `Catch` / `Finally` counts as
  handling it, and a function's final expression is its result);
- a promise used as a value (`load(2) + 1`, `if (load(3))`, `load(4).Body`);
- `Array.Filter`, `Array.Sort` and `Array.Reduce` given an async function (they call it synchronously);
- and hints at `await` of something that is never a promise (a literal, a synchronous function of the file that makes
  no promise).

Promises come from the file's async functions, the Promise module, `Then` / `Catch` / `Finally` and library functions
named `...Async` / `非同步...` or registered with `aqua_define_async`.

## How it works / 運作方式

The compiler lowers `async function` to a call of the runtime builtin `async@` with the function literal and `await x`
to a call of `await@` (names no source can write); `runtime/host/promise.c` implements both and the Promise module.
An async function's call starts the literal with `aqua_invoke_async`, the same path event handlers use, and settles its
promise when that task ends. `await` of a pending promise defers the call (`aqua_defer`), so the task waits exactly
like a script waiting for a network reply, and resumes when the promise settles. Every target runs the same code.

See [examples/async](../examples/async/README.md).
