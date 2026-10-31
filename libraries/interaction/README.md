# interaction

Native-feeling input for applications that draw their own interface with any graphics library. It owns no window and
draws nothing: graphics libraries (Processing, WGPU, GLFW, future ones) draw, this library decides what the user's
input means the way each platform's own applications do. The rules for building such applications, and a checklist for
AI coding assistants, are in [docs/native-like-apps.md](../../docs/native-like-apps.md).

| Module | Written in | What it does |
| --- | --- | --- |
| `Interaction` / `互動` | C (`src/`) | Platform conventions; sessions that turn window events into semantic events (clicks with counts, drags with velocity, long presses, pinches, context menu requests, back and forward, scrolling and zooming, keyboard commands) and call handlers |
| `ScrollView` / `捲動檢視` | AquariusLang (`aqua/`) | Wheel, touch pan and fling, classic or overlay scrollbars, thumb dragging, track paging, keyboard scrolling, scroll into view |
| `Navigation` / `導覽` | AquariusLang | The back stack: screens that can refuse back (unsaved changes), the first screen left to the system |
| `ContextMenu` / `右鍵選單` | AquariusLang | Placement inside the window, highlight, keyboard navigation, platform shortcut labels, closing rules |
| `Focus` / `焦點` | AquariusLang | Tab order and the focus-visible rule |
| `HitTest` / `命中測試` | AquariusLang | Layered hit regions: only the topmost control under the pointer is hovered or pressed; menus and dialogs block what they cover; a pressed control keeps the capture |

## Interaction

| Function | Arguments | Result |
| --- | --- | --- |
| `Conventions()` / `慣例()` | | record: `platform`, `formFactor` (`desktop`/`mobile`), `primaryModifier` (`Ctrl`, `Cmd`, `Ctrl or Cmd`), `doubleClickTime`, `longPressTime` (ms), `dragThreshold`, `touchSlop`, `wheelStep`, `minimumTarget` (logical units), `caretBlinkTime` (ms, 0 = no blinking), `scrollbars` (`classic`/`overlay`), `contextMenuOnRelease`, `hover`, `backButton`, `darkMode`, `reducedMotion`, `highContrast` |
| `Attach(window)` / `附加` | a shared window object (`p.platformWindow`, `wgpuWindow.PlatformWindow`, a GLFW window); on Android it may be omitted | the window's session (one per window) |
| `Create()` / `建立` | | a session without a window, fed by `Feed` |
| `ShortcutLabel(command)` / `快捷鍵標籤` | `"copy"`, `"redo"`, ... | `"Ctrl+C"`, `"⌘C"` on Apple targets, or null |

Session members: `On(type, handler)` / `註冊` returns an id (type `"*"` takes every event), `Off(id)` / `取消註冊`,
`Feed(raw)` / `輸入事件`, `Dispatch()` / `派送`, `SafeArea()` / `安全區域` (`left`, `top`, `right`, `bottom`,
`keyboardVisible`), `SetCursor(name)` / `設定游標` (`arrow`, `text`, `crosshair`, `hand`, `resizeHorizontal`,
`resizeVertical`, `resizeDiagonal`, `resizeAntiDiagonal`, `move`, `notAllowed`; false where there is no cursor),
`CursorArea(name, x, y, width, height, cursor)` / `游標區域` (while the pointer hovers the rectangle the cursor is that
shape, the arrow outside every area; setting a name again moves its area, later areas lie on top, and a handler's
`SetCursor` for the same event still wins), `RemoveCursorArea(name)` / `移除游標區域`, `Detach()` / `解除`,
`TextInput(enabled)` / `文字輸入` (typed text and input-method composition arrive as `text` and `composition` events;
a composition's `phase` is `start`, `update` or `end`, its `text` the uncommitted text and its `cursor` the caret in
it), `TextRect(x, y, width, height)` / `文字位置` (where input-method windows open), property `attached`.

Handlers receive one event and run newest first until one returns `true`. They run at the runtime's next turn after
the window system delivered the input; `Dispatch()` runs them at once (hand-driven frame loops, tests). A `"back"` from
the system that no handler takes closes the activity on Android and leaves the page on the web.

Event types: `press`, `release`, `click`, `move`, `dragStart`, `drag`, `dragEnd`, `longPress`, `pinch`,
`contextMenu`, `scroll`, `zoom`, `key`, `text`, `composition`, `command`, `back`, `forward`, `resize`, `focus`, `visibility`, `pause`,
`resume`. Fields (null when they do not apply): `type`, `time`, `x`, `y`, `startX`, `startY`, `dx`, `dy`,
`velocityX`, `velocityY`, `scale`, `width`, `height`, `button`, `count`, `pointerId`, `pointerType`, `modifiers`,
`shift`, `alt`, `primary`, `key`, `text`, `action`, `command`, `source`, `phase`, `state`. The full table is in the
doc.

`Feed(raw)` takes the events of a graphics library that does not use the SDK's windows, in `aqua/window.h` terms:
`{"type": "pointer" | "button" | "scroll" | "key" | "back" | "resize" | "focus" | "visible" | "lifecycle" |
"captureLost" | "text" | "composition" | "tick", "time": seconds, "x", "y", "pointer", "pointerType": "mouse" | "touch" | "pen", "button",
"action" (0 release, 1 press, 2 repeat), "mods" (GLFW bits), "key" (GLFW key code)}`.

## How it works

`src/recognizer.c` is plain C over the events of `aqua/window.h`: per pointer it tracks the press, the drag threshold
(`touchSlop` for fingers), click series, velocity over the last 100 ms, long-press timers and two-finger pinches;
keys become commands through the X-macro table `src/commands.def`. `src/interaction.c` holds the sessions, queues the
events and calls the handlers with `aqua_invoke_async`. The OS layer is `src/platform/<os>.c`:

| Target | Conventions | Back | Safe area |
| --- | --- | --- | --- |
| Windows | `GetDoubleClickTime`, `SM_CXDRAG`, wheel lines, caret blink, `AppsUseLightTheme`, animations, high contrast; context menus on release | mouse button 4, Alt+Left | none |
| Linux | GTK defaults, `GTK_THEME=...:dark` | mouse button 4, Alt+Left | none |
| Android | `ViewConfiguration` timeouts and touch slop, night mode, animator scale | the back gesture; unhandled: `Activity.finish()` | system bars, cutout, keyboard |
| web | media queries through `web-glue/mediaquery` | the browser's back button through `web-glue/history`; unhandled: `history.back()` | none |

macOS and iOS add `src/platform/macos.c` and `ios.c` (Command as primary, Apple shortcut labels, overlay scrollbars,
iOS's edge swipe as back).

## Tests

`tests/gestures.aqua`, `commands.aqua` and `modules.aqua` feed events and run on every target; `attach.aqua` (and
`attach_wgpu.aqua` on desktop and web) attach to real windows and need `-Devices`.

## Dependencies

None.
