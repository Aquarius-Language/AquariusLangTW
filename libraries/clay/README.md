# clay

`Clay`: Nic Barker's [Clay](https://github.com/nicbarker/clay) (pinned in `third_party/clay`), a fast immediate-mode
flexbox-like layout library, for AquariusLang. A layout declares its elements every frame and returns them as the UI
draw list ([format](../saturn/README.md#the-ui-draw-list)) that `drawUI` or any renderer draws. It mixes with Saturn
and ImGui through `SaturnUI`'s layers.

```aqua
var p = import("Processing");
var Clay = import("Clay");
Clay.SetMeasureText(p.uiTextMetrics);
var layout = Clay.Create(640, 400);
p.run(function() { p.size(640, 400, p.P2D, "Clay"); }, function() {
    p.background(18);
    layout.SetPointer(p.mouseX, p.mouseY, p.isMousePressed);
    layout.Begin();
    Clay.Element({"id": "root", "width": "grow", "height": "grow", "padding": 16, "gap": 16}, function() {
        Clay.Element({"id": "side", "width": Clay.Fixed(180), "height": "grow", "direction": "column", "padding": 12,
                      "background": if (Clay.Hovered()) { "#3d4458"; } else { "#262a36"; }, "radius": 10}, function() {
            Clay.Text("Menu 選單", {"fontSize": 20, "fontWeight": 700, "color": "#ffffff"});
        });
    });
    p.drawUI(layout.End());
});
```

## API

- `Clay.Create(width, height | maxElements)` → a layout: `SetDimensions(w, h)`, `SetPointer(x, y, down)`,
  `Scroll(dx, dy, seconds | dragScrolling)`, `Feed(event)` (Interaction events; returns whether the pointer is over a
  visible element of the last frame), `Begin()`, `End()` (the draw list), `ElementBox(id)`, `PointerOver(id)`,
  `PointerOverIds()`, `SetDebugMode(on)` (Clay's own inspector).
- Between `Begin` and `End`: `Clay.Element(declaration | children)` (children is a function declaring the children),
  `Clay.Open(declaration)` ... `Clay.Close()`, `Clay.Text(text | config)`, `Clay.Hovered()`, `Clay.PointerOver(id)`,
  `Clay.ScrollOffset()`.
- `Clay.SetMeasureText(f(text, family, size, weight, italic))` (the measurer contract Saturn uses; pass
  `p.uiTextMetrics`); without one Clay uses a fixed approximation that is the same on every target.
- Sizes: a number (fixed), `"grow"`, `"fit"`, `"50%"`, or `Clay.Grow(| min, max)`, `Clay.Fit(| min, max)`,
  `Clay.Fixed(n)`, `Clay.Percent(0..1)`.

A declaration is an id (text) or a hash: `id`, `width`, `height`, `padding` (CSS shorthand), `gap`, `direction`
(`row`, `column`), `alignX` (`left`, `center`, `right`), `alignY` (`top`, `center`, `bottom`), `background`, `radius`
(one or `[tl, tr, br, bl]`), `aspectRatio`, `image` (any renderer's image), `border` (`{"color", "width": n or
[top, right, bottom, left], "between"}`), `scroll` (`true`, `"vertical"`, `"horizontal"`) or `clip`, `floating`
(`{"attachTo": "parent" | "root" | "element", "parentId", "offset": [x, y], "expand": [w, h], "zIndex", "element",
"parent" (attach points `leftTop` .. `rightBottom`), "pointerCapture": "passthrough", "clipTo": "parent"}`) and
`embed` (`f(x, y, w, h)` returning another library's draw list, such as a Saturn surface's, drawn in the element).
Text configs: `fontSize`, `fontFamily`, `fontWeight`, `fontStyle`, `color`, `lineHeight`, `letterSpacing`, `wrap`
(`words`, `newlines`, `none`), `align`. Colors are `"#rgb[a]"`, `"#rrggbb[aa]"`, `[r, g, b(, a)]` (0..255), a few
names, or `0xAARRGGBB`.

Bad declarations are errors checked before an element opens, so Clay's tree stays intact. `src/clay.c` is the whole
binding; `tests/layout` covers sizing, wrapping, floating, hover, embedding and errors.
