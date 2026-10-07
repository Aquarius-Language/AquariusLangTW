# Aquarius desktop VM

The desktop executable uses `AquariusLangVM` to compile and execute Aquarius scripts.
It owns the desktop builtins, script runner, REPL, graphics modules and examples.
`runtime/ScriptRunner.cs` parses each script or REPL input and passes its AST to
`VmEvaluator`. Script imports and Processing callbacks also run through the VM.
The REPL preserves its environment between inputs; each file run gets a fresh one.

Includes the array/string builtins, console output, operating system queries,
external process execution, working-directory identifiers, script modules, GLFW,
GLAD, all OpenGL bindings, GLM, STBImage, WGPU, Processing and Jolt Physics. Examples and available
native runtime libraries are copied to both build and publish output.

Run from the repository root with a .NET 8 SDK (which includes the .NET 8 runtime):

```powershell
dotnet run --project AquariusDesktopVMREPL
dotnet run --project AquariusDesktopVMREPL -- AquariusDesktopVMREPL/examples/starship_expedition/main.aqua
dotnet run --project AquariusDesktopVMREPL -- --disassemble AquariusDesktopVMREPL/examples/increment.aqua
```

Native graphics retain the existing platform and driver requirements. Build the
native library using `./native/build.ps1` before graphics execution; the VM desktop
project copies libraries from its own `runtimes/` directory.
See the [native guide](../native/README.md) and
[Processing guide](../AquariusDesktopVMREPL/graphics/Processing.md).
Processing renders with wgpu; GLFW supplies its window/input. The `WGPU` module
also supports headless WGSL rendering and compute without the GLFW bridge.
NuGet restores the pinned wgpu-native runtime binaries and copies them to build
and publish outputs. Keep those files with a distributed application.
See the [wgpu API and migration guide](graphics/WGPU.md) and the `wgpu_compute`,
`wgpu_triangle`, and `processing_wgpu` examples. Raw OpenGL modules remain available.

Import `Jolt` or `JoltPhysics` for headless rigid-body physics. NuGet restores the
pinned .NET 8 binding and native engine; no graphics initialization or CMake build
is needed. See the [Jolt API and testing guide](physics/Jolt.md) and the
`jolt_physics` example. Keep the Jolt native assets and license notices with
published applications.

```powershell
dotnet publish AquariusDesktopVMREPL -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o dist/win-x64/vm
./dist/win-x64/vm/AquariusDesktopVMREPL.exe ./dist/win-x64/vm/examples/increment.aqua
```

The VM core library, installed native libraries and example assets are included
in the output. Distribute the entire output directory and the GLFW, GLAD and
stb_image licenses. See the repository [release guide](../README.md#正式發佈net-自包含部署self-contained).

Without arguments the executable starts the REPL. A script run prints its final
result when one is present. Syntax or runtime errors set exit code 1.
`--disassemble` compiles and prints instructions without executing the script;
it does not load imports or initialize graphics.

## Compiled bottles

Source execution and the REPL still compile directly into memory. Use `-c` to
save bytecode without running any scripts, or `-cr` to save a bottle and then load
and execute it. The default output is the first input's basename with `.bottle`
in the current directory; `-o` selects another output path. The first input is the
default entry point. Other inputs are packaged modules, executed when imported;
they are not automatically run in sequence.

```powershell
./AquariusDesktopVMREPL.exe -c ./test1.aqua ./test/test2.aqua ./test/test3.aqua
./AquariusDesktopVMREPL.exe test1.bottle
./AquariusDesktopVMREPL.exe -cr -o app.bottle ./test1.aqua ./test/test2.aqua ./test/test3.aqua
./AquariusDesktopVMREPL.exe --entry test/test2.rius app.bottle
```

The resulting ZIP-compatible `.bottle` contains:

```text
bottle.json
test1.rius
test/test2.rius
test/test3.rius
```

Paths are relative to the current directory by default, use forward slashes in
the archive, and replace only the source extension. Use `--root directory` to
choose another source root, for example:

```powershell
./AquariusDesktopVMREPL.exe -c --root ./src -o app.bottle ./src/main.aqua ./src/lib/helper.aqua
```

This creates `main.rius` and `lib/helper.rius`. Input paths and the output path
are resolved from the command's current directory, even when `--root` is set.
All inputs must be `.aqua` files under the source root. List every script that
the application imports; imports are resolved at runtime, so dependencies are
not automatically discovered during compilation. Duplicate paths (ignoring
case), sources outside the root, missing sources and syntax errors fail without
replacing an existing bottle. A successful compilation replaces the output.
Use `--` before positional inputs whose names start with `-`.

Run a bottle by passing its path, or use `--entry path/script.rius bottle.bottle`
to select another packaged script. Bytecode loads directly into memory, with no
source parsing or archive extraction. Bottles can be moved and executed after
the `.aqua` sources are removed. Script imports accept `.aqua` or `.rius` names;
relative imports resolve from the importing script's package directory.
`目前工作目錄` is the bottle's containing directory plus the script's relative
directory, so existing imports built with that identifier continue to work.
Module functions and returned closures retain their script's import context.
Missing modules, circular imports and runtime errors return exit code 1.
Successful scripts that produce no final value return exit code 0.
CLI output uses UTF-8, including when redirected to a file or another process.

Native modules such as `GLM`, `GLFW` and `Processing` remain provided by the
desktop runtime. Images and other non-script assets are not bundled; distribute
them beside the bottle using the same relative directories. Native dependencies
are still required for graphics execution.

`bottle.json` identifies the `aquarius-bottle` format, version 1, and entry point.
Each `.rius` is a versioned binary file beginning with `RIUS`, storing instructions,
typed constants and nested function bytecode. Function inspection text is kept
for display; it is never parsed or evaluated. Loaders reject unsupported versions,
malformed instructions, invalid stack/loop control flow and unsafe archive paths.
Version 1 limits a bottle to 10,000 scripts and 256 MiB of uncompressed entries,
with at most 64 MiB and 128 nested bytecode levels per `.rius` file.

When piping Chinese source into the REPL from PowerShell, use matching UTF-8
input and pipe encodings:

```powershell
[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::InputEncoding
@('變數 計數 = 40;', '計數 += 2;', '計數;') | ./dist/win-x64/vm/AquariusDesktopVMREPL.exe
```
