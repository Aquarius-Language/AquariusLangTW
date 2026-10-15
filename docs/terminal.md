# Terminal / 終端機

Core defines `ITerminalBackend`, requests/results, validation, bilingual bindings
and editor metadata. Desktop supplies `DesktopTerminalBackend`; Android supplies
`ProcessBuilder` through the native bridge. Web registers the same module with
`IsSupported() == false` and a clear unsupported error for execution.

```aquarius
變數 terminal = 匯入("Terminal"); # 或 終端機
如果 (terminal.IsSupported()) {
    變數 result = terminal.RunScript("echo hello", {"TimeoutMs":5000});
    印出(result.ExitCode, result.StandardOutput, result.StandardError);
}
# Direct execution preserves argument boundaries, including spaces/metacharacters.
# terminal.Run("python", ["script.py", "a value"], {"WorkingDirectory":"..."});
```

`Run(executable, arguments[, options])` / `執行` launches a process directly, with
no shell expansion. `RunScript(script[, options])` / `執行腳本` explicitly invokes
PowerShell on Windows, `/bin/sh` on Linux/macOS/FreeBSD, or `/system/bin/sh` on
Android. Windows script output uses UTF-8. Relative executable paths containing a
separator and working-directory paths resolve through the host's resource mapper.
Bare executable names use the OS search path. Existing `execFile` remains available.

Options are `TimeoutMs` (integer 1..3,600,000; default 60,000) and
`WorkingDirectory`. Unknown options and wrong types are errors before launch.
Results expose `ExitCode` / `結束代碼`, `StandardOutput` / `標準輸出`,
`StandardError` / `標準錯誤`, and `TimedOut` / `已逾時`. A timeout returns exit code
-1. Nonzero child exit codes are ordinary results. OS launch failures become
Aquarius errors. Standard input is closed; this API runs noninteractive scripts.

Both output streams drain concurrently, retaining at most the first 1 MiB of
characters per stream on desktop (1 MiB of bytes on Android). Desktop timeout
termination includes the process tree. Android terminates the direct child; child
processes remain subject to the Android application UID and OS restrictions.
Output collection is also bounded if inherited pipes remain open. No detached
terminal window is opened. Applications requiring live input should use a
dedicated host integration.

The process has its application's permissions. Browser execution is unavailable;
Android can execute only commands permitted by its sandbox and executable-code
policy. Restoring/building Bottle packages never invokes Terminal or install hooks.
