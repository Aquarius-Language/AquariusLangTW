# aqua, the command line

`aqua` compiles AquariusLang to WebAssembly, runs applications on the native runtime, exports them to every platform,
and manages [Bottle](../AquariusBottle/README.md) projects and libraries.

```powershell
aqua build main.aqua lib/tools.aqua --root . --assets assets -o app.wasm
aqua run app.wasm [argument ...]                 # the runtime pack of this machine; returns the program's exit code
aqua run main.aqua                               # compiles main.aqua and the scripts it names, then runs it
aqua build app.wasm --target web -o site
aqua build app.wasm --target windows -o app.exe [--hide-terminal true] [--onnx-cuda true]
aqua build app.wasm --target linux -o app
aqua build app.wasm --target android -o app.apk [--application-id org.example.app] [--app-name Name]
aqua build main.aqua --target web -o site        # sources build straight to a target too
aqua new MyApp; aqua new MyLibrary --type library
aqua build MyApp [--target PLATFORM] [--locked] [--offline]
aqua library build path/to/library --targets windows-x64,web
aqua repl
```

- The default target is `wasm`. `--root` and repeatable `--assets` apply to source builds; `--entry` selects the
  starting script. `aqua build` compiles exactly the sources it is given (list every module, including imports by
  computed paths); `aqua run main.aqua` also compiles the scripts its string literals name.
- Sources inside a library folder (`manifest_version = 2` with `[library]`, such as its `tests/`) build as plain
  programs that can import the library; a project folder or `aqua.toml` builds the project.
- `aqua run` passes `--library` for the external libraries of the project around the application.
- `aqua repl` replays the session: each input is compiled after the earlier accepted inputs and the program runs again,
  showing only the new input's output and value. Earlier inputs therefore run again each time (their output is hidden,
  but their side effects, such as files they write, repeat). An input that fails is left out of the session.
- Exit codes: 0 success, 1 compilation, runtime or I/O failure, 2 invalid command or options; `aqua run` returns the
  application's.

See [build targets](../AquariusBuild/README.md), [the compiler](../AquariusCompiler/README.md) and
[libraries](../../docs/external-libraries.md).
