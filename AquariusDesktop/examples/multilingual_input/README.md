# Multilingual input / wgpu

Run from the repository root after rebuilding the native bridge:

```powershell
./native/build.ps1
dotnet run --project AquariusDesktop -- AquariusDesktop/examples/multilingual_input/main.aqua
```

Type with the system keyboard or IME. Committed text is appended through
`textInput`; the composition preview is drawn separately with wgpu-backed
Processing text. Backspace deletes one grapheme, including combining accents
and emoji sequences. Close the window to exit.

Windows supports IME previews and candidate positioning. Linux/macOS receive
GLFW's committed Unicode text, but this bridge does not expose preedit or caret
positioning there. Processing's existing non-Windows bitmap font has limited
glyph coverage; use a suitable text renderer in a custom wgpu integration.

See the [text input API](../../graphics/TextInput.md).
