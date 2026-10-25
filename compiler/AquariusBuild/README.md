# AquariusBuild

The build targets: they turn an application `.wasm` into what a platform runs, from the runtime packs of the
installation (`runtime/<target>/` in a distribution, `.native-build/install/<target>/` in a checkout, built by
`scripts/build-runtime.ps1`). `BuildTargets.Default` registers them; a new platform adds a target here and needs no
command-line changes. Builds never run application code, and replace their output only after they completed.

| Target | Output |
| --- | --- |
| `windows` | `app.exe`: the pack's `aquarius.exe` with the application appended (overlay format 2, `ExecutableBundle`: wasm, `{"EntryPoint":...}`, a 64-byte footer with the SHA-256 and `AQUARIUS-APP-V2!`). The pack's other files (DLLs, licenses) are copied beside it, external libraries' `aqua_<name>.dll` into `libraries/`. `--hide-terminal true` (or `[desktop] hide_terminal = true`) marks it a GUI program. |
| `linux` | the same with `aquarius` and `libaqua_<name>.so`, marked executable |
| `web` | a static site: `index.html` and `app.mjs` (web-glue/page), `glue/` (web-glue), `runtime/` (the web pack: `aquarius.wasm`, `libc.so`, `libc++.so`, `libc++abi.so`) with only the needed `runtime/libraries/<name>.wasm`, `program.wasm` with its assets in `assets/`, and `aquarius.json` (`{program, entry, assets}`) |
| `android` | a signed APK for both ABIs (`android-arm64-v8a`, `android-x86_64` packs): `lib/<abi>/libaquarius.so` and the pack's other shared libraries, external `libaqua_<name>.so`, `classes.dex`, the manifest with the permissions of the needed libraries, `assets/aquarius/program.wasm` and its assets; stored entries aligned with `zipalign -P 16`, signed by `apksigner` with `~/.aquarius/android/debug.keystore` or `--keystore`/`--key-alias` (passwords from `AQUARIUS_KEYSTORE_PASSWORD` / `AQUARIUS_KEY_PASSWORD`). Needs `ANDROID_HOME` (build-tools, a platform) and a JDK. |

`--onnx-cuda true` (or `[onnx] cuda = true`) builds Windows and Linux executables from the `<target>-cuda` pack, built
with `scripts/build-runtime.ps1 -OnnxCuda` (ONNX Runtime's CUDA build; see `libraries/onnx/README.md`).

`NativeRuntime` runs the pack of this machine for `aqua run`, the REPL, the tests and the language server's catalog
(`aquarius [--library file ...] --catalog`).
