# Aquarius desktop VM

The desktop executable uses `AquariusLangVM` to compile and execute Aquarius scripts.
It owns the desktop builtins, script runner, REPL, graphics modules and examples.
`runtime/ScriptRunner.cs` parses each script or REPL input and passes its AST to
`VmEvaluator`. Script imports and Processing callbacks also run through the VM.
The REPL preserves its environment between inputs; each file run gets a fresh one.

Includes the array/string builtins, console output, operating system queries,
external process execution, working-directory identifiers, script modules, GLFW,
GLAD, all OpenGL bindings, GLM, STBImage and Processing. Examples and available
native runtime libraries are copied to both build and publish output.

Run from the repository root with a .NET 8 SDK and .NET 6 Runtime:

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

When piping Chinese source into the REPL from PowerShell, use matching UTF-8
input and pipe encodings:

```powershell
[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::InputEncoding
@('變數 計數 = 40;', '計數 += 2;', '計數;') | ./dist/win-x64/vm/AquariusDesktopVMREPL.exe
```
