# terminal

`Terminal` / `終端機`: child processes a script asks for explicitly, on Windows, Linux and Android. On the web the module
loads, `IsSupported()` is false and running fails with `Terminal processes are unavailable on this platform.`

```aquarius
變數 terminal = 匯入("Terminal");   # or 終端機
如果 (terminal.IsSupported()) {
    變數 result = terminal.RunScript("echo hello", {"TimeoutMs": 5000});
    印出(result.ExitCode, result.StandardOutput, result.StandardError, result.TimedOut);
}
terminal.Run("python", ["script.py", "a value"], {"WorkingDirectory": "tools"});
```

| Member | Behavior |
| --- | --- |
| `Platform` / `平台` | `windows`, `linux`, `android`, `web` |
| `IsSupported()` / `是否支援()` | whether processes can run here |
| `Run(executable, arguments[, options])` / `執行` | starts the program directly; every argument arrives exactly (no shell) |
| `RunScript(script[, options])` / `執行腳本` | PowerShell on Windows (UTF-8 output), `/bin/sh -c` on Linux, `/system/bin/sh -c` on Android |
| `Start(executable, arguments[, options])` / `啟動` | starts the program and returns a `Terminal.Process` at once; its output reaches `OnOutput` / `OnError` as it arrives |
| result `ExitCode`/`結束代碼`, `StandardOutput`/`標準輸出`, `StandardError`/`標準錯誤`, `TimedOut`/`已逾時` | |
| process `Wait()`/`等待結束`, `WaitAsync()`/`非同步等待結束`, `Kill()`/`結束`, `Running`/`執行中`, `Result`/`結果` | the result once the output is collected (a promise from `WaitAsync`); `Kill` ends the process tree and returns whether it was running; `Result` is null while it runs |

```aquarius
var server = terminal.Start("aqua", ["run", "main.aqua"], {"WorkingDirectory": "app",
    "OnOutput": text => print("out:", text), "OnError": text => print("err:", text)});
await Promise.Delay(5000);
server.Kill();
print(server.Wait().ExitCode);
```

Options: `TimeoutMs` (integer 1..3,600,000, default 60,000; `Start` has no limit unless given), `WorkingDirectory` and
`Environment` (a map of variable names to strings the child gets on top of the runtime's environment; `null` leaves a
variable out; names compare without case on Windows); `Start` also takes `OnOutput` and `OnError`, functions called with each piece of text (whole UTF-8 sequences) on the
runtime's thread, one call at a time per process and in order (a callback may be an async function that waits, such
as one sending the text over a network; the next piece waits for it). Unknown options and wrong types fail
before anything starts, with the old messages (`Terminal / 終端機.Run: Unknown terminal option.`). Executable paths
containing a separator and working directories that name a packaged asset resolve to the asset's file. A timeout ends
the whole process tree and returns exit code -1 with `TimedOut` true. Nonzero exit codes are ordinary results; launch
failures are errors. Standard input is an empty, closed pipe. Each stream keeps the first 1,048,576 UTF-16 code units
and keeps draining after that. If processes the program left running keep its output pipes open longer than
`max(1000, TimeoutMs)` ms after it exits (a background child, or Windows Error Reporting's `WerFault.exe` after a
crash), collection stops there: the result has the program's exit code and the output that arrived until then, and
those processes keep running.

## How it works

`src/terminal.c` validates, builds the request and returns a deferred call (or, for `Start`, a `Terminal.Process`); the platform layer launches the process on
the runtime thread (so launch errors report directly) and a worker thread collects both streams, the exit, the timeout
and cancellation together, then posts the result:

- `src/platform/windows.c`: `CreateProcessW` with only the three standard handles inherited
  (`PROC_THREAD_ATTRIBUTE_HANDLE_LIST`), overlapped named pipes for output, a job object for the process tree, and
  CommandLineToArgvW quoting of every argument (the program name always quoted, as .NET did).
- `src/platform/posix.c` (Linux, Android): `fork` + `execve` (the program found in `PATH` before forking), a new process
  group for the tree, an error pipe reporting `execve` failures, and `poll` over both pipes.

When the runtime ends, running processes are killed and their workers joined.

## Dependencies

None: Win32 and winpthreads (linked statically) on Windows, POSIX and pthreads elsewhere.

## Differences from the old implementation

- Wrong argument counts report the runtime's message (`Terminal.Run expects 2 to 3 arguments, got 1.`) instead of
  `Terminal / 終端機.Run: Wrong argument count.`
- The output limit counts UTF-16 code units on every target (Android counted bytes).
- Android ends the whole process group on a timeout (it ended only the direct child).
- Launch failures carry the operating system's text (`An error occurred trying to start process 'x' with working
  directory 'y'. …`), close to .NET's wording.
- A program killed by a signal reports exit code 128 + the signal number (as .NET and Java did).
- Results are native objects: `型別(result)` shows `Terminal.Result` instead of `module`.

## Tests

`tests/processes.aqua` (echo, exit codes, closed standard input, argument boundaries, working directory, timeout),
`tests/streaming.aqua` (`Start`: streamed output in order, waiting callbacks, `Wait` / `WaitAsync`, `Kill`, a child
left running with the output),
`tests/environment.aqua` (variables set and left out for one child only),
`tests/validation.aqua` (desktop and Android) and `tests/unsupported.aqua` (web).
