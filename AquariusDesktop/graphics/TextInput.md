# Multilingual text input

`匯入("TextInput")` provides renderer-independent Unicode text entry for a live
GLFW window, including `GLFW_NO_API` windows used by wgpu. It does not initialize
GL, GLAD, or a GPU. Rebuild `native/build.ps1` (or CMake on other platforms) before
using the new native exports. Use these calls on the GLFW-owning thread.

| TextInput member | Behavior |
| --- | --- |
| `Start(window)` | Enables text events for a focused text field; repeated calls are harmless |
| `Stop(window)` | Cancels Windows composition, disables events, clears pending events |
| `SetCaretRect(window,x,y,width,height)` | Positions Windows IME UI at a caret in client coordinates, using the same units as `GLFW.GetWindowSize`; dimensions must be nonnegative |
| `Poll(window)` | Drains ordered events after `GLFW.PollEvents()`; does not pump OS events itself |
| `SupportsComposition()` | True for this bridge's Windows IMM implementation; false on other platforms |
| `Backspace(text)` | Removes the final Unicode grapheme, preserving surrogate pairs, combining accents and emoji sequences |

Each event is a module with `type`, `text`, and `cursor` fields:

| Type | Text | Cursor |
| --- | --- | --- |
| `textInput` | Committed Unicode text to insert | 0 |
| `compositionStarted` | Empty | 0 |
| `compositionUpdated` | Current preedit preview; replace the previous preview | UTF-16 code-unit offset within the preview |
| `compositionEnded` | Empty; clear the preview | 0 |

Commit boundaries are implementation-dependent; concatenate `textInput` payloads
without assuming one event per word or IME transaction. Never append preedit to
the document. Draw it separately, and let the renderer handle glyph shaping,
fallback fonts and bidirectional layout as needed. No keyboard-code-to-character
mapping or Unicode normalization is applied.

The queue has a 1 MiB safety limit and reports a language error on overflow or
allocation failure instead of returning silently truncated text. Poll regularly.
Disabling input and destroying/recreating a window clears its queue and native
composition state. `Start`/`Stop` govern event delivery; they do not switch the
system keyboard layout or globally disable the user's IME.

Raw window setup, suitable for attaching a wgpu surface in an embedding host:

```text
變數 glfw = 匯入("GLFW");
變數 input = 匯入("TextInput");
glfw.Init();
glfw.WindowHint(glfw.GLFW_CLIENT_API, glfw.GLFW_NO_API);
變數 window = glfw.CreateWindow(800, 600, "Unicode / wgpu");
input.Start(window);
input.SetCaretRect(window, 24, 80, 1, 24);
glfw.PollEvents();
變數 events = input.Poll(window);
# Consume event.text when event.type is "textInput"; draw with your renderer.
input.Stop(window);
glfw.DestroyWindow(window);
glfw.Terminate();
```

The language's raw WGPU module currently renders offscreen; input does not add
a new raw surface/presentation API. Processing already owns a wgpu surface and
offers the following sketch integration:

```text
變數 p = 匯入("Processing");
變數 value = "";
p.run(函式() {
    p.size(800, 240);
    p.startTextInput();
    p.on("textInput", 函式() { value = value + p.inputText; });
}, 函式() {
    p.background(24); p.fill(255); p.textSize(24);
    p.text(value, 24, 80);
    p.text(p.compositionText, 24 + p.textWidth(value), 80);
    p.textInputRect(p.ceil(24 + p.textWidth(value)), 56, 1, 28);
});
```

`p.startTextInput()`, `p.stopTextInput()`, and `p.textInputRect(x,y,w,h)` require
an active sketch. `p.textInputEnabled`, `p.inputText`, `p.compositionText`,
`p.compositionCursor`, and `p.isComposing` expose state. Register the four event
names above with `p.on(name,callback)`; callbacks have no arguments. Processing
owns its queue; use callbacks instead of `TextInput.Poll` on a sketch window.
Text and composition events request a redraw even under `noLoop()`.

Existing `keyTyped` callbacks still receive each committed Unicode scalar;
append from either `textInput` or `keyTyped`, since using both duplicates text.
Physical key state remains queryable with `keyDown`, but `keyPressed` and
`keyReleased` callbacks are suppressed in frames with active/changing Windows
composition so IME navigation does not trigger editor shortcuts. Automatic Esc
exit is disabled while text input is enabled, allowing Esc to cancel IME input;
use `exit()` or the window close button to leave the sketch.

Windows preedit/result handling uses
[IMM composition strings](https://learn.microsoft.com/en-us/windows/win32/api/immdev/nf-immdev-immgetcompositionstringw)
and [candidate positioning](https://learn.microsoft.com/en-us/windows/win32/api/imm/nf-imm-immsetcandidatewindow).
The OS retains its candidate UI; Processing draws the preedit. Losing focus ends
the preview without committing it. Linux/macOS use
[GLFW's Unicode character callback](https://www.glfw.org/docs/3.4/input_guide.html#input_char)
for committed text only; native preedit and caret positioning are unavailable.
Existing Processing font rendering on those hosts uses a limited bitmap font.

Try the [interactive example](../examples/multilingual_input/README.md).
