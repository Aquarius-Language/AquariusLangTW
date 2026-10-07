# Aquarius desktop VM

The desktop executable uses `AquariusLangVM` to compile and execute Aquarius scripts.
It links the existing desktop builtin, import, REPL and graphics source files, so
both desktop hosts share the same library implementations. The `AQUARIUS_VM`
compile symbol routes script execution, script imports and Processing callbacks
through `VmEvaluator`.

Includes the array/string builtins, console output, operating system queries,
external process execution, working-directory identifiers, script modules, GLFW,
GLAD, all OpenGL bindings, GLM, STBImage and Processing. Examples and available
native runtime libraries are copied to both build and publish output.

Run from the repository root with a .NET 8 SDK and .NET 6 Runtime:

```powershell
dotnet run --project AquariusDesktopVMREPL
dotnet run --project AquariusDesktopVMREPL -- AquariusDesktopInterpretedREPL/examples/starship_expedition/main.aqua
dotnet run --project AquariusDesktopVMREPL -- --disassemble AquariusDesktopInterpretedREPL/examples/increment.aqua
```

Native graphics retain the existing platform and driver requirements. Build the
native library using `./native/build.ps1` before graphics execution; the VM desktop
project links libraries from `AquariusDesktopInterpretedREPL/runtimes/`.
See the [native guide](../native/README.md) and
[Processing guide](../AquariusDesktopInterpretedREPL/graphics/Processing.md).

```powershell
dotnet publish AquariusDesktopVMREPL -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o dist/win-x64/vm
./dist/win-x64/vm/AquariusDesktopVMREPL.exe ./dist/win-x64/vm/examples/increment.aqua
```

The core libraries, native libraries and example assets are included in the output.
Distribute the entire output directory and the same third-party native licenses as
the interpreted desktop package.
