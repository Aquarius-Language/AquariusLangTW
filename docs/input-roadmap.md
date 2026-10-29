# Input and interaction: state and what to improve

Where keyboard, pointer and text input stand across the runtime's window layer (`aqua/window.h`), the `interaction`
and `input` libraries, Processing's input and the embedded editor, and what is worth doing next. See
[native-like applications](native-like-apps.md) for the rules applications follow.

## Done in this round (2026-10-29)

- **Keypad navigation keys.** With Num Lock off, keypad keys arrive as the navigation keys printed under them
  (7 Home, 1 End, 9 Page Up, 3 Page Down, 8/2/4/6 arrows, 0 Insert, the decimal point Delete) on Windows, Linux,
  Android and the web; the scancode keeps the physical key, a held key releases as what it pressed, and
  `aqua_window_key` reports the navigation key as held. One shared helper (`host_keypad_key`) serves the native layers;
  the web glue takes the browser's own key. Every library sees it: Interaction's `key` and commands, Processing's
  `keyCode`, the editor.
- **Android key map.** Home, End, Page Up/Down, Insert, the keypad keys, brackets, backslash, grave, Caps/Num/Scroll
  Lock, Print Screen, Pause, Meta and Menu now map to GLFW codes; Meta, Caps Lock and Num Lock reach `mods`.
- **Keypad Enter** acts as Enter in the editor (Interaction's `activate` command already took both).
- **Cursor areas** (`CursorArea`), the composition caret (`cursor`) and inline composition in the editor; Escape in
  Processing respects other libraries' text input.

## Keyboard

1. **Text-navigation commands with each platform's keys.** Editors now read raw keys, so Apple conventions are wrong:
   ⌘← should go to the line start and ⌥← by word, while Ctrl+← (word) and Home are right only off Apple.
   Add to `commands.def`: `lineStart`, `lineEnd`, `documentStart`, `documentEnd`, `wordLeft`, `wordRight`,
   `deleteWordLeft`, `deleteWordRight`, `deleteLineLeft` (⌘⌫), `pageUp`, `pageDown`, with Shift extending the
   selection (an `extend` flag on the command event instead of a second table row per command). The editor then
   handles commands, not keys, and gains the right behaviour everywhere at once.
2. **Insert / overtype.** Insert toggles overtype mode on Windows and Linux (block caret, typing replaces the next
   character). The keys arrive; the editor needs the mode.
3. **Key repeat and dead keys.** Repeat comes from the OS on desktop and the web, but not for Android hardware keys
   held down; dead keys (´ + e → é) depend on the text events, which work on desktop and web; Android's hardware
   keyboard path should route through its key character map for dead keys too.
4. **Layout-aware shortcut keys.** Shortcuts match GLFW key codes, which follow the US layout's positions on the web
   (`KeyboardEvent.code`). On AZERTY, Ctrl+Z is the physical W key on the web but Z on desktop. Matching shortcuts on
   the layout's character (`e.key` / `glfwGetKeyName`) for letters would make them agree.
5. **Media and browser keys** (volume, play/pause, browser back/forward keys) are dropped; the back/forward keys
   should become `back` / `forward` events like mouse buttons 4 and 5.
6. **Function keys F13–F24** on Android and the keypad `=` / `,` are partly mapped; finish the tables.

## Text input

1. **Full-width (全形) text.** Fixed: on high-density displays text drawn with fonts that round advances per size
   (細明體, the `monospace` of `zh-Hant` pages) was wider than `textWidth()`, so carets and following text drifted;
   Processing now fits such text to its measured box. A crisper fix on the web is rasterizing with the box's width
   (`fillText` with `maxWidth`) instead of scaling the image.
2. **Composition attributes.** Windows and the web know which part of the composition is the converted segment;
   exposing segment ranges (`composition` event `segments`) would let editors draw the thick underline under the
   active clause, as native editors do.
3. **Reconversion and surrounding text.** IMEs ask for the text around the caret to reconvert a selection
   (`WM_IME_REQUEST` on Windows, `InputConnection.getTextBeforeCursor` on Android). Without it, Android's keyboard
   suggestions and auto-capitalisation work poorly. A `TextContext(before, after)` call on the session would supply it.
4. **Touch selection handles** (long press selects a word, handles adjust it, a small Cut / Copy / Paste bar): the
   rules describe them; the editor does not draw them yet.
5. **Clipboard on the web.** Reading the clipboard is asynchronous in browsers; a paste started inside a synchronous
   `Dispatch()` fails (seen in the editor's web test). Commands from real input run asynchronously and work; the
   `paste` command could carry the pasted text from the browser's `paste` event so no read is needed at all.
6. **Accessibility.** Nothing drawn on a canvas reaches screen readers. A small accessibility tree (roles, names,
   focus) mirrored into hidden DOM elements on the web, UI Automation on Windows and `AccessibilityNodeInfo` on
   Android is the largest missing piece for "native-like".

## Pointer and touch

1. **Cursor areas follow hover only.** During a drag the cursor stays what it was, as native applications do; areas
   could also take a `pressed` shape (closed hand while dragging a grab area).
2. **Pen.** Pressure and tilt fields exist in `aqua_window_event`; only the web fills tilt. Windows Ink (`WM_POINTER`)
   and Android `MotionEvent` axes should fill them, and Interaction could expose `pressure` / `tilt` on its events.
3. **Precision touchpads.** Pinch arrives as Ctrl+wheel on Windows and the web (Interaction turns it into `zoom`);
   native rotation and the three-finger gestures are not available through GLFW.
4. **Pointer lock and raw motion** exist in the window layer (cursor mode); Interaction has no API for them, so games
   use Processing's or GLFW's directly.

## By library

| Library | Gaps |
| --- | --- |
| `interaction` | Text-navigation commands with `extend`; composition segments; text context; accessibility tree; pen fields on events; layout-aware shortcuts |
| `input` (`AndroidInput`) | Hardware keyboard repeat and dead keys; surrounding text for the IME; it overlaps with Interaction on Android, so new work should go to Interaction and `AndroidInput` stay for Android-only features (vibration, insets) |
| Processing (`graphics`) | Done: `INSERT`, `HOME`, `END`, `PAGE_UP`, `PAGE_DOWN` constants. Open: function-key constants (`F1`...) |
| Editor (`Aqua_Embedded_LSP_Editor`) | Commands instead of raw keys; overtype; touch handles; composition segments; find/replace UI for the `find` commands Interaction already sends |
| Web glue (`window`) | Paste text from the `paste` event; media/browser keys as back/forward |
| Android layer | Repeat, dead keys, `getTextBeforeCursor`, pen axes |
| Desktop layer | Windows Ink pen data; composition segments from `GCS_COMPATTR` |

## Suggested order

1. Text-navigation commands with `extend`, and the editor on commands (fixes Apple conventions, removes key tables
   from every editor).
2. Paste text with the `paste` command on the web; composition segments.
3. Text context for IMEs (Android keyboards, Windows reconversion).
4. Touch selection handles in the editor.
5. Accessibility tree, starting with the web (hidden DOM mirror).
