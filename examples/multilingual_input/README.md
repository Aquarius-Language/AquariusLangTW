# Multilingual input / wgpu

```powershell
./native/build.ps1
dotnet run --project AquariusDesktopVMREPL -- examples/multilingual_input/main.aqua
```

This Processing sketch draws committed Unicode text and Windows IME preedit
through wgpu. Backspace removes one grapheme. Close the window to exit.

See the [full example notes](../../AquariusDesktopVMREPL/examples/multilingual_input/README.md)
and [text input API](../../AquariusDesktopVMREPL/graphics/TextInput.md), including
Linux/macOS composition and font limitations.
