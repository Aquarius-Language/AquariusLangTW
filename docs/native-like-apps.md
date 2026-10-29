# Native-like applications / 原生感應用程式

AquariusLang applications draw their own interface with a graphics library (Processing, WGPU, OpenGL or a later one).
A canvas knows nothing about scrollbars, right clicks, text selection, the back gesture or dark mode: every behavior
that a platform's own controls give for free has to be built on purpose when the application wants it. This document
lists those behaviors, the rules each platform follows, and how the default libraries provide them.

**When to use it.** These are recommendations for applications that should feel like the platform's own applications:
tools, editors, forms, lists, settings, document and productivity applications. They are not requirements of the
language. Creative coding, generative art, games, visualizations, demos, kiosks, terminal programs and anything with a
deliberately custom feel are free to ignore any or all of it; take only the pieces that help (a game may still want
the back gesture or pause handling).

**For AI coding assistants ("vibe coding").** Decide first whether the user wants a native-like application. Apply
[the checklist](#the-checklist) when they ask for an app, tool or interface that should behave like the platform's own
software, or when it is clearly that kind of program; implement the items that apply, with the libraries named there.
Do not apply it to sketches, art, games, graphics experiments or terminal programs unless the user asks for these
behaviors, and when it is unclear, ask or mention the option instead of adding it.

Targets today: Windows, Linux, Android and the web. macOS and iOS are planned; their rules are listed so that
applications written now already behave correctly there.

## Contents

1. [How the pieces fit](#how-the-pieces-fit)
2. [The checklist](#the-checklist)
3. [Rules in detail](#rules-in-detail)
4. [Platform reference](#platform-reference)
5. [Library reference](#library-reference)
6. [Connecting a graphics library](#connecting-a-graphics-library)
7. [Pitfalls](#pitfalls)

## How the pieces fit

```text
 OS input ─▶ aqua/window.h events ─▶ Interaction session ─▶ your handlers ─▶ your state ─▶ graphics library draws
   (mouse, touch, keys, back,        (recognizer: clicks,      (ScrollView, Navigation,
    resize, lifecycle)                drags, long press, ...)    ContextMenu, Focus, TextEdit)
```

- **The graphics library draws** and owns the window. It hands the window to other libraries as a shared window
  object: `p.platformWindow` (Processing), `window.PlatformWindow` (WGPU), the window `GLFW.CreateWindow` returns.
- **[`Interaction`](../libraries/interaction/README.md)** turns raw input into what it means on this platform: a click
  with its click count, a drag that only starts past the platform's threshold, a long press, a pinch, a context menu
  request, back, a keyboard command (`copy`, `undo`, `focusNext`, ...), scrolling in logical units. It also reports the
  platform's conventions (double-click time, touch slop, scrollbar style, dark mode, ...).
- **Behavior modules** written in AquariusLang keep the state of common controls and take Interaction events:
  `ScrollView`, `Navigation`, `ContextMenu`, `Focus` (library `interaction`), `TextEdit` (library `application`) for
  editing text, `TextInput` (library `graphics`) for input methods.
- **Your application** keeps its state in plain values and redraws from it. Nothing in this chain depends on one
  graphics library, so moving an application from Processing to WGPU or to a future renderer changes only the drawing.

A minimal application:

```aqua
var p = import("Processing");
var ix = import("Interaction");
var sv = import("ScrollView");
var nav = import("Navigation");

var session = null;
var list = null;
var stack = nav.Create("home");

p.run(function() {
    p.size(480, 800, p.P2D, "Notes");
    session = ix.Attach(p.platformWindow);
    list = sv.Create(0, 0, p.width, p.height, p.width, 5000);
    session.On("back", nav.Handler(stack));                         # Android back, browser back, mouse back, Alt+Left
    session.On("resize", function(e) { sv.Place(list, 0, 0, e.width, e.height); return false; });
    session.On("*", function(e) { return sv.Handle(list, e); });    # wheel, touch fling, scrollbar, keys
    session.On("click", function(e) { print("open item at", e.y + list.y); return true; });
}, function() {
    sv.Update(list, 1.0d / 60);
    p.background(255);
    # ... draw the visible rows shifted by -list.y, then the scrollbar from sv.VerticalBar(list)
});
```

## The checklist

For native-like applications (see [when to use it](#native-like-applications--原生感應用程式)): go through the lines and
apply those that fit the application. The libraries named do the platform-specific part.

### Every native-like application

- [ ] **Attach an Interaction session** to the window (`ix.Attach(p.platformWindow)`) and route input through it
      instead of reading raw mouse and key state for interface controls.
- [ ] **Logical units everywhere.** Lay out in logical units (`p.width`, event `x`/`y`), never in device pixels; draw
      crisp on high-density screens (`pixelDensity`, `PixelWidth`).
- [ ] **Relayout on `resize`.** Windows are resized, phones rotate, browser tabs change size. Nothing may be laid out
      once at start-up.
- [ ] **Targets big enough to hit:** at least `Conventions().minimumTarget` (24 on desktop, 48 on Android, 44 on iOS
      and touch browsers) for anything clickable or tappable, spacing included.
- [ ] **Buttons act on `click`, not `press`.** A press shows the pressed state; the action happens on `click` (the
      release over the same control). Dragging away cancels it (the recognizer reports `dragStart` instead of `click`).
- [ ] **Hover and pressed states** for every interactive element on devices with hover (`Conventions().hover`); never
      make information reachable only by hovering, since touch screens have no hover.
- [ ] **Cursor shapes** (`session.CursorArea`, or `session.SetCursor` while dragging): `text` over editable text,
      `hand` over links, `resizeHorizontal` / `resizeVertical` over splitters and edges, `arrow` elsewhere (over
      line numbers, scrollbars and popups too). Keep the areas current when the layout changes.
- [ ] **Scrolling** for anything that can be longer than the window, with `ScrollView`: wheel and touchpad, Shift+wheel
      sideways, touch pan with fling, scrollbars of the platform's style, keyboard scrolling, and
      `ScrollIntoView` for the focused item.
- [ ] **Zoom** for zoomable content (maps, images, documents): `zoom` events (Ctrl/Cmd+wheel) and `pinch` events, and
      the `zoomIn`, `zoomOut`, `zoomReset` commands.
- [ ] **Context menus** where the content has actions (text, list items, files, canvas objects), with
      `ContextMenu`: opened by `contextMenu` events (right button, long press, Menu key, Shift+F10), showing shortcut
      labels, closing on Escape, back and outside presses.
- [ ] **Keyboard:** every control reachable with Tab / Shift+Tab (`Focus`), a visible focus ring after keyboard use
      only, Enter or Space activates, Escape cancels (closes the topmost menu, dialog or search first).
- [ ] **Shortcuts** through `command` events (`copy`, `cut`, `paste`, `undo`, `redo`, `selectAll`, `find`, `save`,
      `new`, `open`, `close`, `zoomIn`, ...), never by testing Ctrl yourself: Apple targets use Command, and the web
      takes both. Show them with `ix.ShortcutLabel`.
- [ ] **Text fields** use `TextEdit` (editing, undo, word moves, clipboard) and `TextInput` (input method composition,
      the candidate window, the soft keyboard). See [Text](#text).
- [ ] **Back** (`back` events) goes back one level, in this order: close the open menu, dialog or popup, leave search
      or selection mode, return to the previous screen (`Navigation`). On the first screen do not handle it: the
      system closes the app (Android) or leaves the page (web).
- [ ] **Lifecycle:** save state on `pause` (Android, and `visibility` false on the web and desktop) and restore it
      when the application starts again: the screen, scroll positions, drafts. Stop animations and audio while hidden.
- [ ] **Unsaved work** is never lost silently: intercept closing (`Window.Attach(...)` and `ResolveClose` in the
      graphics library) to ask, and keep drafts in `Storage`.
- [ ] **Safe areas:** keep content and controls out of `session.SafeArea()` (status and navigation bars, notches,
      rounded corners) and above the soft keyboard (`keyboardVisible`).
- [ ] **Follow the user's settings** from `Conventions()`: `darkMode` (a dark theme), `reducedMotion` (no parallax,
      zooming transitions or long animations; ScrollView already stops flinging), `highContrast` (solid borders,
      no information in color alone), and the platform's `doubleClickTime` and thresholds (the recognizer uses them).
- [ ] **Accessibility:** publish the controls with `Application.Accessibility` (roles, labels, values) so screen
      readers can use a drawn interface; contrast of at least 4.5:1 for text.
- [ ] **Redraw only when something changed** or while `ScrollView.Update` (or another animation) returns true; keep
      handlers short, so input never waits for a frame.

### Desktop (Windows, Linux, macOS, desktop browsers)

- [ ] Right click opens the context menu (on release on Windows, on press elsewhere; the recognizer knows which).
- [ ] Double click selects a word or opens an item, triple click selects a line or paragraph (`count` of `press`).
- [ ] Mouse buttons 4 and 5 and Alt+Left / Alt+Right (Command+[ and ] on macOS) go back and forward (`back`,
      `forward` events).
- [ ] Classic scrollbars stay visible when content overflows; their thumb drags and their track pages.
- [ ] Windows resize freely down to a sensible minimum; F11 (Ctrl+Command+F on macOS) toggles fullscreen
      (`fullscreen` command); remember the window size and position in `Storage`.
- [ ] The title shows the document's name and an unsaved-changes marker; Ctrl/Cmd+W closes, Ctrl/Cmd+S saves.
- [ ] Files can be dropped onto the window when the application opens files (`fileDrop` of the graphics library's
      `Window` integration).
- [ ] Tooltips on hover after a short delay for icon-only buttons, including the shortcut label.

### Mobile (Android, iOS, touch browsers)

- [ ] Long press replaces right click: it opens the context menu or starts selection (`longPress`, `contextMenu` with
      `source` `touch`), with a short vibration on Android (`AndroidInput.Vibrate(20)`).
- [ ] Lists and pages pan with the finger and fling (`ScrollView`); scrollbars are thin overlays that fade.
- [ ] No hover-only interface; tap targets of at least `minimumTarget`.
- [ ] Back works everywhere (Android back gesture, and the browser's back button on the web), see the order above.
- [ ] Pinch zooms zoomable content; double tap zooms in on maps and images.
- [ ] Content stays inside `SafeArea()`; text fields being edited stay above the soft keyboard (scroll them into view
      when `keyboardVisible` turns true).
- [ ] Orientation changes and split screen are `resize` events: relayout, keep the scroll position and the input.
- [ ] `pause` saves everything: Android may end a paused application without warning.

### Web

- [ ] The browser's back button is `back` (the session keeps a history entry while you handle back); unhandled back
      leaves the page, as users expect.
- [ ] `Conventions().formFactor` tells phones (`mobile`, coarse pointer) from computers; lay out for both.
- [ ] Shortcuts: the browser keeps some (Ctrl+T, Ctrl+W, Ctrl+N in most browsers); offer menu items or buttons for
      every action, not only shortcuts.
- [ ] The page may be closed at any time: save drafts as they change (`Storage`), and ask before leaving with
      `ResolveClose("cancel")` of the graphics library's `Window` integration while there are unsaved changes.

## Rules in detail

### Pointer input

| Rule | Why | How |
| --- | --- | --- |
| Distinguish click from drag by distance, not time | A slightly moving finger or mouse is still a click | `click` only when the pointer stayed within `dragThreshold` (mouse, pen) or `touchSlop` (finger); otherwise `dragStart`, `drag`, `dragEnd` |
| Count clicks with the user's double-click time | Users set it for their needs | `press.count` (1, 2, 3, ...) uses `doubleClickTime` and a small distance |
| Act on release | Lets users cancel by moving away | Use `click` for actions, `press` for pressed visuals and for starting selections |
| Fling with real velocity | Lists that stop dead feel broken on touch screens | `dragEnd.velocityX/Y` (logical units per second over the last 100 ms, 0 if the finger rested) |
| One gesture at a time | A second finger turns a pan into a pinch | The recognizer ends the first finger's drag (`dragEnd`, phase `cancel`) and reports `pinch` start, change, end |
| Cancel cleanly | Losing focus or pausing in the middle of a drag must not leave it stuck | `dragEnd` with phase `cancel`; treat it like Escape |

### Scrolling

- Wheel and touchpad scroll the view under the pointer, not the focused one. When a view is already at its end it does
  not take the event, so an outer view scrolls (`ScrollView.Handle` returns false: scroll chaining).
- Shift+wheel scrolls sideways (Windows, Linux, browsers; macOS does it in the system).
- Primary modifier + wheel zooms (`zoom` events with `scale` per notch) around the pointer.
- Touch: content follows the finger exactly (`ScrollBy(-dx, -dy)`), then flings; a tap during a fling stops it and
  does nothing else.
- Keys when the view has focus: arrows by a line (40), Page Up/Down and Space/Shift+Space by 87.5% of a page,
  Home/End (Ctrl/Cmd+Home/End) to the ends.
- Scrollbars: classic on Windows, Linux and desktop browsers (always visible when the content overflows, wider on
  hover, thumb at least 24 long, a press on the track pages toward the pointer); overlay on Android, iOS, macOS and
  touch browsers (thin, shown while scrolling, fading after about a second, not draggable by touch).
- Selecting text with the mouse past the view's edge scrolls the view (auto-scroll): while a selection drag is outside
  the view, call `ScrollBy` each frame toward the pointer, faster the farther it is.
- Keep scroll positions when going back to a screen and when the window resizes.

### Text

- Use `TextEdit` (library `application`) for the text and its editing rules and `TextInput` (library `graphics`) for
  input methods: composition text is shown underlined at the caret and is not part of the text until committed, and
  the candidate window is placed with the caret rectangle.
- Clicking places the caret; Shift+click extends the selection; dragging selects (`dragStart`/`drag` with the press's
  `count`): by characters after one press, by words after a double click, by lines or paragraphs after a triple
  click. Double click selects a word, triple click a line or paragraph.
- Keyboard: arrows move, Shift extends, Ctrl+arrows (Option+arrows on macOS) move by words, Home/End to line ends,
  Ctrl/Cmd+A selects all; `copy`, `cut`, `paste`, `undo`, `redo` commands go to `TextEdit` (`editor.Copy()`, ...).
  The keypad's keys with Num Lock off arrive as Home, End, Page Up/Down, the arrows, Insert and Delete, and the
  keypad's Enter is Enter.
- Touch: a tap places the caret and shows the soft keyboard (`TextInput` / `AndroidInput.ShowKeyboard`); a long press
  selects the word and shows handles to adjust the selection and a small menu (Cut, Copy, Paste, Select all); the
  field scrolls above the soft keyboard.
- The caret blinks with `caretBlinkTime` (0 means the user turned blinking off) and stays solid while typing.
- The text cursor (`CursorArea(name, x, y, width, height, "text")`) shows over editable and selectable text.
- Input-method composition (Chinese, Japanese, Korean...) shows while it is typed: draw the `composition` event's
  uncommitted `text` at the caret, underlined, with the rest of the line moved aside and the caret at its `cursor`;
  keys during a composition belong to the input method, and the text is inserted only when the `text` event commits
  it.

### Context menus

- Opened by `contextMenu` events: right button (Windows on release, other desktops on press), long press on touch,
  the Menu key or Shift+F10 (then `x` is null: open it at the focused item).
- Contents depend on what is under the pointer; selection-dependent items are disabled rather than hidden when they
  are the usual ones (Cut, Copy, Paste), so the menu keeps its shape.
- Show shortcut labels (`ShortcutLabel`) at the right of items; separators between groups; at most one level of
  submenus.
- Keep the menu inside the window: it opens to the left or upward near the edges (`ContextMenu.Open` does this).
- Close on Escape, back, a press outside (which does nothing else), losing focus and resizing.
- On touch screens use the platform's touch target height for rows; lifting the finger that long-pressed chooses
  nothing.

### Back and navigation

- Android: the back gesture (and button) must always work. The first screen does not handle it, so the activity
  closes. iOS (planned): the edge swipe from the left goes back inside navigation stacks.
- Web: the browser's back button is the same as Android back while the application handles back; history entries
  for in-application screens let users go back as they expect.
- Desktop: mouse buttons 4/5 and Alt+Left/Right (Command+[ and ] on macOS) go back and forward where there is
  navigation (browsers, file managers, settings, wizards).
- Order: the innermost temporary thing closes first (menu, popup, dialog, search, selection mode), then the screen.
  Register handlers so the innermost has the newest handler (`session.On` while it is open, `session.Off` when it
  closes): handlers run newest first.
- Never trap users: a screen may ask about unsaved changes once (`Navigation.SetBackHandler` returning true), but a
  second back goes back.

### Windows, lifecycle and system settings

- `resize` carries `width`, `height` (logical) and `scale` (pixels per unit): relayout and redraw. It also comes when
  only the scale changes (another monitor, a display resolution or scaling change, page zoom), so layouts and cached
  text or images made for one density are redone there, never at startup only.
- Layouts survive any size, down to nothing: clamp computed sizes at 0 (`max(0, height - toolbar)`), hide what does not
  fit (hints, secondary panels) instead of overlapping it, and keep the application running; empty areas draw nothing.
  Processing's `clip`, boxed `text` and shapes accept empty sizes, and `ScrollView.Place` and `WebFrame.Place` treat
  negative sizes as empty.
- `focus` false: stop key repeats, end drags (the recognizer does), dim selection highlights like native controls.
- `visibility` false / `pause`: stop animations, audio and polling; save state. `resume`: refresh time-dependent
  content.
- Closing: desktop windows ask before losing unsaved work (`Window.ResolveClose`); Android cannot be asked, so save on
  `pause`; the web can only ask the browser to confirm (`ResolveClose("cancel")` turns on `beforeunload`).
- `Conventions()` is read fresh each time, so check `darkMode` and `reducedMotion` on `resume` and `focus`.

### Accessibility

- Every control has a role and a label in `Application.Accessibility`, updated when it changes; focus order matches
  the visual order.
- Text contrast at least 4.5:1 (3:1 for large text and control borders); in `highContrast` use solid colors and
  outlines.
- Never rely on color alone, on hover alone or on gestures alone (offer buttons for swipe actions).
- Respect `reducedMotion`.

## Platform reference

| | Windows | Linux | macOS (planned) | Android | iOS (planned) | Web |
| --- | --- | --- | --- | --- | --- | --- |
| Shortcut modifier | Ctrl | Ctrl | Cmd | Ctrl (hardware keyboards) | Cmd (hardware keyboards) | Ctrl or Cmd |
| Context menu | right release, Menu, Shift+F10 | right press, Menu, Shift+F10 | right press, Ctrl+click | long press | long press | right press, long press |
| Back | mouse 4, Alt+Left | mouse 4, Alt+Left | Cmd+[ , swipe | back gesture | edge swipe | browser back, mouse 4 |
| Scrollbars | classic | classic | overlay | overlay | overlay | classic, overlay on touch |
| Double click | user setting | 400 ms | user setting | 300 ms double tap | 300 ms | 500 ms |
| Drag threshold | `SM_CXDRAG` (4) | 8 | 3 | touch slop (8 dp) | 10 | 4, 10 for touch |
| Minimum target | 24 | 24 | 24 | 48 dp | 44 pt | 24, 44 for touch |
| Wheel step | lines × 16 | 48 | system | 64 | | 100 |
| Fullscreen | F11 | F11 | Ctrl+Cmd+F | immersive mode | | F11 |
| Redo | Ctrl+Y, Ctrl+Shift+Z | Ctrl+Shift+Z, Ctrl+Y | Cmd+Shift+Z | | | both |
| Quit | Alt+F4 (system) | Ctrl+Q | Cmd+Q | back on the first screen | home | close the tab |

The values with "user setting" are read from the system by `Interaction.Conventions()`; the others are the platform's
defaults.

## Library reference

### Interaction / 互動

```aqua
var ix = import("Interaction");
var c = ix.Conventions();               # platform, formFactor, primaryModifier, doubleClickTime, longPressTime,
                                        # dragThreshold, touchSlop, wheelStep, caretBlinkTime, minimumTarget,
                                        # scrollbars, contextMenuOnRelease, hover, backButton, darkMode,
                                        # reducedMotion, highContrast
var session = ix.Attach(p.platformWindow);   # or ix.Create() for a library that feeds its own events
var id = session.On("click", function(e) { ...; return true; });   # true: handled, later handlers are skipped
session.Off(id);
session.Dispatch();                     # run pending handlers now (hand-driven loops)
var area = session.SafeArea();          # left, top, right, bottom, keyboardVisible
session.SetCursor("text");
session.CursorArea("editor", 0, 40, 600, 560, "text");   # the text cursor while hovering it, the arrow elsewhere
ix.ShortcutLabel("save");               # "Ctrl+S", "⌘S"
```

Handlers run newest first; returning `true` stops the event there. Returning anything else passes it on. Register
`"*"` to receive every event (behavior modules use this). A system `back` nobody handles gets the system's behavior.

| Event | When | Fields |
| --- | --- | --- |
| `press` | a button or finger goes down | `x`, `y`, `button` (0 left/finger, 1 right, 2 middle, 3 back, 4 forward), `count`, `pointerId`, `pointerType` (`mouse`, `touch`, `pen`) |
| `release` | it goes up | same as `press` |
| `click` | released without dragging or long-pressing | same; `count` is 2 for a double click |
| `move` | a mouse or pen moves with nothing pressed (hover) | `x`, `y`, `pointerType` |
| `dragStart`, `drag` | moved past the threshold, then every move | `x`, `y`, `startX`, `startY`, `dx`, `dy` (since the previous event), `button`, `count` |
| `dragEnd` | released, or cancelled | `phase` (`end`, `cancel`), `velocityX`, `velocityY` |
| `longPress` | a finger or pen held still for `longPressTime` | `x`, `y`, `pointerType` |
| `pinch` | two fingers | `phase` (`start`, `change`, `end`, `cancel`), `scale` (since the previous event), `x`, `y` (center), `dx`, `dy` |
| `contextMenu` | right button, long press, Menu key, Shift+F10 | `x`, `y` (null from the keyboard), `source` (`mouse`, `touch`, `pen`, `keyboard`) |
| `scroll` | wheel or touchpad | `dx`, `dy` (logical units toward the content's end), `x`, `y` (pointer), `source` |
| `zoom` | primary modifier + wheel | `scale`, `x`, `y` |
| `key` | every key press, repeat and release | `key` (GLFW code), `action` (`press`, `repeat`, `release`), `text`, `shift`, `alt`, `primary` |
| `command` | a platform shortcut | `command`: `copy`, `cut`, `paste`, `undo`, `redo`, `selectAll`, `find`, `findNext`, `findPrevious`, `new`, `open`, `save`, `saveAs`, `print`, `close`, `quit`, `preferences`, `refresh`, `zoomIn`, `zoomOut`, `zoomReset`, `fullscreen`, `help`, `rename`, `delete`, `cancel`, `activate`, `focusNext`, `focusPrevious`, `nextTab`, `previousTab` |
| `back`, `forward` | system back, mouse buttons 4 and 5, Alt+Left/Right, Cmd+[ / ] | `source` (`system`, `mouse`, `keyboard`) |
| `resize` | the window's size or density changed | `width`, `height`, `scale` |
| `focus`, `visibility` | the window gained or lost focus, was shown or hidden | `state` |
| `pause`, `resume` | the application went to the background or came back (Android) | |

Every event also has `time` (seconds), `modifiers` (GLFW bits) and `shift`, `alt`, `primary`.

### ScrollView / 捲動檢視

```aqua
var sv = import("ScrollView");
var view = sv.Create(left, top, width, height, contentWidth, contentHeight);
session.On("*", function(e) { return sv.Handle(view, e); });   # pass "key" events only while the view has focus
sv.Place(view, left, top, width, height);      # on resize
sv.SetContentSize(view, contentWidth, contentHeight);
sv.ScrollTo(view, x, y); sv.ScrollBy(view, dx, dy); sv.ScrollIntoView(view, x, y, w, h);
var animating = sv.Update(view, seconds);      # each frame: momentum and fading scrollbars
var bar = sv.VerticalBar(view);                # visible, x, y, width, height, thumbX, thumbY, thumbWidth,
                                               # thumbHeight, opacity, active (being dragged)
# draw the content translated by (-view.x, -view.y) and clipped to the view, then the bars
```

### Navigation / 導覽

```aqua
var nav = import("Navigation");
var stack = nav.Create("home");
session.On("back", nav.Handler(stack));
nav.OnChange(stack, function(action, page) { redraw(); });   # "push", "pop", "reset"
var page = nav.Push(stack, "editor", {"id": 3});
nav.SetBackHandler(page, function(page) { if (dirty) { askToDiscard(); return true; } return false; });
nav.Current(stack).name; nav.Depth(stack); nav.CanGoBack(stack); nav.Pop(stack); nav.Reset(stack);
```

### ContextMenu / 右鍵選單

```aqua
var cm = import("ContextMenu");
var menu = cm.Create(function(command) { run(command); });
cm.Add(menu, "cut", "Cut", hasSelection);
cm.Add(menu, "copy", "Copy", hasSelection);
cm.Add(menu, "paste", "Paste", true);
cm.AddSeparator(menu);
cm.Add(menu, "selectAll", "Select All", true);
session.On("contextMenu", function(e) { cm.Open(menu, e.x, e.y, p.width, p.height, e.time); return true; });
session.On("*", function(e) { return cm.Handle(menu, e); });    # registered after the others: it goes first
# draw while menu.open: a box at (menu.x, menu.y) of menu.width x cm.Height(menu); each item i at
# cm.ItemTop(menu, i) with item.label, item.shortcut, item.enabled, item.separator, and i == menu.highlighted
```

### Focus / 焦點

```aqua
var focus = import("Focus");
var ring = focus.Create();
focus.Add(ring, "name"); focus.Add(ring, "email"); focus.Add(ring, "send");
focus.OnChange(ring, function(id) { /* scroll it into view, show the keyboard for text fields */ });
session.On("*", function(e) { return focus.Handle(ring, e); });   # Tab, Shift+Tab; presses hide the ring
focus.Set(ring, "email", false);         # a click focused it
if (focus.RingVisible(ring) && focus.Has(ring, "send")) { /* draw the focus ring */ }
```

## Connecting a graphics library

A graphics library needs to do one of two things to work with every library above:

1. **Built on the SDK's windows (`aqua/window.h`)**: give scripts the shared window object,
   `aqua_window_object(window)`, as a property (`platformWindow` in Processing, `PlatformWindow` in WGPU). Scripts
   pass it to `Interaction.Attach`. Nothing else is needed: Interaction listens with `aqua_window_listen` alongside
   the graphics library.
2. **With windows of its own** (a future renderer, a UI toolkit, a test): create a session with `Interaction.Create()`
   and feed it raw events in the terms of `aqua/window.h`:

   ```aqua
   session.Feed({"type": "button", "time": t, "x": x, "y": y, "pointer": 0, "pointerType": "mouse",
                 "button": 0, "action": 1, "mods": 0});
   ```

   Types: `pointer` (moves), `button` (`action` 1 press, 0 release), `scroll` (`x`, `y` wheel notches, positive `y`
   away from the user), `key` (`key` GLFW code, `action` 0/1/2, `mods`), `back`, `resize` (`x`, `y` = size),
   `focus`, `visible`, `lifecycle` (`action` 1 resumed, 0 paused), `captureLost`, and `tick` (only advances `time`,
   for long presses). Coordinates are logical units; `time` is seconds on one monotonic clock.

Behavior modules never draw and never import a graphics library, and a new graphics library never needs to change
them.

## Pitfalls

- Handlers must return `true` only when they used the event; returning true from a catch-all `"*"` handler hides
  every event from older handlers.
- In a native-like interface, do not read `p.mouseX`/`p.mousePressed` for controls: it bypasses thresholds, click
  counting, cancelling and touch rules. Sketches, art and games are free to use raw input.
- Do not hard-code Ctrl, double-click times, pixel sizes or scrollbar styles: read `Conventions()` and use `command`
  events.
- Do not open a context menu on left press-and-hold with a mouse, or on right click on touch-only devices: follow
  `contextMenu` events.

---

## 繁體中文摘要

星泉應用程式以繪圖函式庫（Processing、WGPU、OpenGL 或未來的函式庫）自行繪製介面，因此捲軸、右鍵選單、拖曳選取文字、
雙擊、長按、雙指縮放、系統返回鍵、深色模式、安全區域等原生行為需要刻意實作。本文件是建議而非強制規範：只有當應用程式
希望像平台原生軟體（工具、編輯器、表單、清單、設定等）時，才依上方[檢查清單](#the-checklist)挑選適用項目實作；創意程式、
生成藝術、遊戲、視覺化、圖學實驗或終端機程式可完全不採用，或只取需要的部分。AI 程式助理應先判斷使用者是否要原生感應用
程式，不確定時詢問或提出選項，而不是自動加入。

- **`interaction` 函式庫**（與繪圖函式庫無關）：`Interaction`／`互動`（C）把視窗事件轉成各平台原生語意的事件（點擊次數、
  拖曳門檻與速度、長按、雙指縮放、右鍵選單請求、返回與前進、捲動與縮放、鍵盤指令），並提供平台慣例（雙擊時間、觸控門檻、
  捲軸樣式、深色模式、減少動態效果等）；`ScrollView`／`捲動檢視`、`Navigation`／`導覽`、`ContextMenu`／`右鍵選單`、
  `Focus`／`焦點` 以星泉撰寫，只管行為與狀態，不負責繪圖。
- **串接方式**：使用 SDK 視窗的繪圖函式庫提供共用視窗物件（Processing 的 `platformWindow`、WGPU 的 `PlatformWindow`），
  交給 `Interaction.Attach`；自有視窗的函式庫以 `Interaction.Create()` 建立工作階段，再用 `Feed` 餵入原始事件。
- **處理器**：`session.On(類型, 函式)` 由最新註冊者先執行，回傳 `真` 表示已處理；系統返回若無人處理，Android 會關閉活動、
  網頁會離開頁面。
- **注意**：不要直接讀取原始滑鼠狀態做介面控制；不要寫死 Ctrl、
  像素尺寸或捲軸樣式。
