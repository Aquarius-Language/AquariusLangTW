# imgui

`ImGui`: Dear ImGui (the docking branch, pinned in `third_party/imgui`) for AquariusLang. It draws nothing and owns no
window: a context renders each frame into the UI draw list ([format](../saturn/README.md#the-ui-draw-list)) that the
graphics library's `drawUI` (or any renderer) draws, and takes its input from Interaction events. It mixes with
Saturn and Clay through `SaturnUI`'s layers.

```aqua
var p = import("Processing");
var ImGui = import("ImGui");
var Interaction = import("Interaction");
var gui = ImGui.CreateContext();
gui.AddDefaultFont(15);
var cjk = ImGui.SystemFontPath("cjk");
if (cjk != null) { gui.AddFont(cjk, 16, true); }       # merged: Chinese labels work
var speed = 1.0d;
p.run(function() {
    p.size(800, 500, p.P2D, "Tools");
    Interaction.Attach(p.platformWindow).On("*", function(e) { return gui.Feed(e); });
}, function() {
    p.background(30);
    gui.NewFrame(p.width, p.height, 1.0d / 60);
    if (ImGui.Begin("Tweaks 調整")) {
        speed = ImGui.SliderFloat("Speed", speed, 0.0d, 10.0d);
        if (ImGui.Button("Reset")) { speed = 1.0d; }
    }
    ImGui.End();
    p.drawUI(gui.Render());
});
```

## Contexts

`ImGui.CreateContext(| {"docking": true, "keyboard": true, "theme": "dark" | "light" | "classic"})`: several can exist
(one per 3D panel); the one in `NewFrame` is current. Members: `NewFrame(width, height | seconds, framebufferScale)`,
`Render(| embed)` (the frame's draw list; `embed(id, x, y, w, h)` returns the list of each `ImGui.Embed` item, spliced
there and clipped to it), `EndFrame()`, `SetCurrent()`, `Destroy()`, input `Feed(event)` (Interaction events, or
hashes with `type`, `x`, `y`, `button`, `dx`, `dy`, `key`, `action`, `text`, `modifiers`; returns whether ImGui wants
it), `AddMousePos`, `AddMouseButton`, `AddMouseWheel`, `AddKey(ImGui.Key_*, down)`, `AddKeyCode(glfwKey, down |
mods)`, `AddText`, `AddFocus`, `HitTest(x, y)` (a window covers the point), fonts `AddDefaultFont(| size)` and
`AddFont(bytes | asset path | file path, size | merge)` (returns the index for `ImGui.PushFont`), `ImGui.SystemFontPath("sans" |
"mono" | "cjk")`, `SetClipboardHandlers(get, set)`, `ImeRect()`, and properties `WantCaptureMouse`,
`WantCaptureKeyboard`, `WantTextInput`, `Framerate`, `MouseCursor` (Interaction's cursor names).

Font atlas textures go to the renderer through the draw list's texture ops (ImGui's `RendererHasTextures` protocol),
so glyphs load on demand, CJK included, and only changed regions are sent.

## Widgets

Every `ImGui.*` function keeps Dear ImGui's name, so its documentation and demo apply. Aquarius has no pointers:

- an editor takes the current value and returns the new one (`v = ImGui.DragFloat("v", v)`, arrays for `Float2..4`,
  `Int2..4`, `ColorEdit3/4`), and `ImGui.IsItemEdited()` tells whether it changed;
- `Begin`, `CollapsingHeader`, `BeginTabItem` and `BeginPopupModal` take an optional `open` Boolean (a close button) and
  report it after the call through `ImGui.LastOpen()`;
- `RadioButton(label, current, value)` returns the new current value; `Combo` and `ListBox` take an array of texts and
  return the index;
- text is shown as given (no printf formatting); slider and drag formats are ImGui's;
- colors are `[r, g, b(, a)]` (0..1 for editors; draw lists also take 0..255), `"#rrggbb(aa)"` or `0xAARRGGBB`;
- images are any renderer's image values (`ImGui.Image(img, w, h)`), or a draw-list texture id.

Covered: windows and child windows, docking (`DockSpaceOverViewport`, `DockSpace`, `SetNextWindowDockID`), layout and
cursor, ids, text, buttons, checkboxes, radios, progress bars, images, combos, list boxes, selectables, drags,
sliders, inputs (text, multiline, hint, numbers), color editors and pickers, trees, headers, tabs, tables, menus and
menu bars, popups, modals and context menus, tooltips, plots, item and window queries, keyboard and mouse queries,
drag and drop (payloads are any values), styles (`StyleColors*`, `PushStyleColor/Var`, `SetStyle({...})`,
`SetStyleColor`, `ScaleAllSizes`), fonts, the demo, metrics, debug log, ID stack, about and style editor windows, and
draw lists (`GetWindowDrawList`, `GetForegroundDrawList`, `GetBackgroundDrawList` with `AddLine`, `AddRect(Filled)`,
`AddCircle(Filled)`, `AddText`, `AddPolyline`, `AddConvexPolyFilled`, `AddBezierCubic`, `AddImage`, paths, clip rects,
channels; valid during their frame). `ImGui.Embed(id, w, h)` reserves an item another library draws (see Render).

Misuse never aborts: assertions become errors of the call (`src/aqua_imconfig.h`), widget calls outside a frame are
errors, and mismatched Begin/End are repaired by Dear ImGui's error recovery.

## Layout

`src/context.cpp` (contexts, input, fonts, rendering), `src/widgets.cpp`, `src/drawlist.cpp`, `src/constants.def`
(ImGui's enumerations as module constants, `ImGui.WindowFlags_NoResize`, `ImGui.Key_A`, ...),
`src/aqua_imconfig.h`, `third_party/imgui/` (README.md there). Tests: `tests/context`.
