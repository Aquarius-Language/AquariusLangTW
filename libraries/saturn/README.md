# saturn

Saturn / 土星 lays out user interfaces with Meta's Yoga engine and paints them into a render-agnostic **UI draw list**
that any graphics library draws: the graphics library's `drawUI` on a window, an offscreen canvas (a texture for a 3D
object) or a PNG, or a renderer of your own. It has two layers:

| Module | Written in | What it does |
| --- | --- | --- |
| `Saturn` / `土星` | C++ (`src/`) over Yoga (`third_party/yoga`) | Yoga's node and config API under Yoga's names, CSS-named styles, text, images, transitions, painting, hit testing, painters |
| `SaturnUI` / `土星介面` | AquariusLang (`aqua/SaturnUI.aqua`) | Flutter-style widgets, themes and style sheets, events, surfaces, layers that mix in ImGui and Clay, `RunApp` |

```text
aqua.toml            manifest: the native module and the SaturnUI module
CMakeLists.txt       src/ and Yoga's sources, Yoga's symbols kept private (YG_EXPORT=)
src/saturn.cpp       entry, Saturn.Config, Saturn.Node (lifecycle, tree, layout, callbacks, properties.def)
src/style.cpp        SetStyle / ReplaceStyle, colors, visual and text keys, inheritance, transitions
src/text.cpp         text nodes: measuring through the text measurer, line breaking, ellipsis
src/paint.cpp        painting into the draw list, hit testing, Saturn.Painter, Saturn.Combine
src/constants.def    Yoga's enumerations (module constants and CSS names)
src/properties.def   Yoga's style properties (generated methods and SetStyle keys)
aqua/SaturnUI.aqua   the widget layer
third_party/yoga/    Yoga, pinned (README.md there)
tests/               <case>.aqua + <case>.expected
```

## Node trees (`Saturn`)

Every `YGNode*` / `YGConfig*` function exists on `Saturn.Node` / `Saturn.Config` objects under its Yoga name without the
prefix, so Yoga code translates line by line:

```aqua
var Saturn = import("Saturn");
var config = Saturn.Config();
config.SetPointScaleFactor(2);
var root = Saturn.Node(config);
root.SetFlexDirection(Saturn.FlexDirectionRow);
root.SetWidth(400);
root.SetPadding(Saturn.EdgeAll, 8);
root.SetGap(Saturn.GutterColumn, 8);
var side = Saturn.Node(config);
side.SetWidthPercent(30);
var body = Saturn.Node(config);
body.SetFlexGrow(1);
body.SetMeasureFunc(function(width, widthMode, height, heightMode) { return [120, 40]; });
root.InsertChild(side, 0);
root.InsertChild(body, 1);
root.CalculateLayout(Saturn.Undefined, Saturn.Undefined, Saturn.DirectionLTR);
print(body.GetComputedLeft(), body.GetComputedWidth(), body.GetComputedLayout());
```

- **Constants**: every Yoga enumeration value as `Saturn.<Name>` (`Saturn.AlignCenter`, `Saturn.EdgeTop`,
  `Saturn.UnitPercent`, `Saturn.Undefined`, ...). Setters also take the CSS name (`SetAlignItems("center")`), and
  sizes take strings (`SetWidth("50%")`, `"auto"`, `"max-content"`, `"fit-content"`, `"stretch"`).
  `Saturn.EnumName("Align", value)` names a value.
- **Style properties** (`src/properties.def`): `Set/Get` for Direction, FlexDirection, JustifyContent/Items/Self,
  AlignContent/Items/Self, PositionType, FlexWrap, Overflow, Display, BoxSizing, Flex, FlexGrow, FlexShrink,
  AspectRatio; FlexBasis, Width, Height with `Percent`, `Auto`, `MaxContent`, `FitContent`, `Stretch` variants;
  Min/Max sizes; Position, Margin (with `Auto`) and Padding per edge; Border; Gap per gutter; the grid properties
  (`SetGridColumnStart`, `SetGridTemplateColumnsCount`, `SetGridTemplateColumn(i, type, value)`, ...). Getters of
  sizes return `Saturn.Value` objects (`value`, `unit`, `unitName`).
- **Tree**: `InsertChild(child | index)`, `SwapChild`, `RemoveChild`, `RemoveAllChildren`, `SetChildren(array)`,
  `GetChild`, `GetChildCount`, `GetChildren`, `GetParent`/`GetOwner`, `Clone` (copies the whole subtree), `Free`,
  `FreeRecursive`, `IsFreed`, `Reset`, `CopyStyle`. A tree lives while the script reaches its root.
- **Layout**: `CalculateLayout(| width, height, direction)`, `GetComputedLeft/Top/Right/Bottom/Width/Height`,
  `GetComputedLayout()`, `GetComputedMargin/Border/Padding(edge)`, `GetHadOverflow`, `IsDirty`, `MarkDirty`,
  `GetHasNewLayout`/`SetHasNewLayout`.
- **Callbacks** (script functions): `SetMeasureFunc(f(width, widthMode, height, heightMode) -> [w, h])`,
  `SetMinContentMeasureFunc`, `SetBaselineFunc(f(width, height))`, `SetDirtiedFunc(f(node))`, config
  `SetLogger(f(level, message, node))`. Errors in callbacks become errors of the call that ran them.
- **Config**: `SetUseWebDefaults`, `SetPointScaleFactor`, `SetErrata`, `SetExperimentalFeatureEnabled`, getters.
- Yoga's assertions are checked first and reported as errors (a child with two parents, a cycle, children of a
  measured node), so no script aborts the runtime.
- Grid: the pinned Yoga stores the grid properties but its layout algorithm does not place grid items yet; SaturnUI's
  `Grid` widget lays out rows of flexible cells.

### Styles

`node.SetStyle(hash)` sets any number of properties by CSS name; `node.ReplaceStyle(style | [style, ...])` makes the
node's style exactly the merged list (later entries win, everything left out returns to its default) and dirties
layout only if it changed. Null values are skipped, and structs work like hashes.

| Keys | Values |
| --- | --- |
| layout | every property above in camelCase (`flexDirection`, `justifyContent`, `width`, `minHeight`, `aspectRatio`, `position` (`relative`, `absolute`, `static`), `display` (`flex`, `none`, `contents`, `grid`), `overflow` (`visible`, `hidden`, `scroll`), ...) |
| edges | `margin`, `padding`, `inset`, `borderWidth` (a number or `[vertical, horizontal]`, `[top, horizontal, bottom]`, `[top, right, bottom, left]`), `marginTop`, `paddingX`, `marginHorizontal`, `borderLeftWidth`, ..., and `top`, `left`, `right`, `bottom`, `start`, `end` |
| gaps, grid | `gap` (or `[row, column]`), `rowGap`, `columnGap`, `gridTemplateColumns` / `gridTemplateRows` / `gridAutoColumns` / `gridAutoRows` (`"100 1fr minmax(50, 2fr) 20% auto"` or arrays), `gridColumn` / `gridRow` (`"1 / span 2"`), `gridColumnStart`, ... |
| visual | `background` (a color, or `{"from", "to", "angle"}` for a linear gradient), `borderColor` (one or four, CSS order), `borderTopColor`, ..., `border` (`{"width", "color"}`), `borderRadius` (one or `[tl, tr, br, bl]`), `borderTopLeftRadius`, ..., `shadow` / `boxShadow` (`{"x", "y", "blur", "spread", "color"}` or an array), `opacity`, `visibility`, `visible`, `pointerEvents` (`none`), `zIndex`, `cursor`, `translateX`, `translateY`, `scale`, `scaleX`, `scaleY`, `rotate` (degrees), `transform` (a hash of those), `transformOrigin` (`[x, y]` fractions), `image`, `objectFit` (`fill`, `contain`, `cover`, `none`, `scale-down`), `tint`, `scrollX`, `scrollY`, `transition` (ms, or `{"duration", "easing": "easeOut" \| "linear" \| "easeInOut" \| "easeIn"}`) |
| text | `color`, `fontFamily`, `fontSize`, `fontWeight` (number, `bold`, `normal`, `light`), `fontStyle` (`italic`), `lineHeight` (a multiple of the size), `textAlign` (`left`/`start`, `center`, `right`/`end`), `maxLines`, `textOverflow` (`ellipsis`), `whiteSpace` (`nowrap`) |

Colors are `"#rgb"`, `"#rgba"`, `"#rrggbb"`, `"#rrggbbaa"`, `"rgb(...)"`, `"rgba(..., 0.5)"`, names (`white`, `red`,
`transparent`, ...), `[r, g, b(, a)]` (0..255), `{"r", "g", "b", "a"}` or `0xAARRGGBB` integers (Processing's
`color()`). Text properties inherit as in CSS: a node's text takes what it leaves unset from its nearest ancestor that
sets it (`maxLines` and `textOverflow` do not inherit). With `transition`, `ReplaceStyle` animates colors, opacity,
radii, transforms and shadows from what is shown, on the clock `Saturn.SetTime(ms)` sets; `Saturn.Animating()` says
whether the last paint was mid-transition.

### Text, images, painting, hit testing

- `node.SetText(text)` makes a text node (Yoga node type text, measured by Saturn); `GetText()`, `GetTextLines()`
  (`text`, `x`, `y`, `width`, `height`, `baseline` per laid-out line). Lines break between words, around CJK
  characters (not before closing or after opening punctuation), at newlines, and between characters for a word wider
  than the line.
- **The text measurer**: `Saturn.SetTextMeasurer(f(text, family, size, weight, italic) -> [width, ascent, descent])`;
  pass `p.uiTextMetrics` (graphics) so layout matches what `drawUI` draws. Without one Saturn uses a fixed
  approximation that is identical on every target (tests). `Saturn.MeasureText(text | style)`, `ClearTextCache()`.
- `node.SetImage(image | width, height)` or the `image` style key: any renderer's image value (a Processing image or
  canvas for `drawUI`); its `width`/`height` members give the intrinsic size `objectFit` uses.
- `node.SetPainter(f(painter, width, height))`: drawing of your own in the node's coordinates, between its
  background and its children.
- `node.Paint(| x, y)` returns the draw list of the tree; `HitTest(x, y)` the deepest node at a point (transforms,
  clips, scrolling, `pointerEvents: none`, `display: none` and `visibility: hidden` respected); `HitTestPath(x, y)`
  the nodes from the root; `GetAbsoluteLayout()`; `GetContentSize()`; `SetScrollOffset(x, y)`; `GetCursor()`;
  `Paints()` (whether the node draws something itself).
- `Saturn.Painter()` builds draw lists by hand: `Rect`, `Circle`, `Border`, `Line`, `Shadow`, `Gradient`, `Text`,
  `Image`, `Mesh`, `Texture`, `FreeTexture`, `PushClip`/`PopClip`, `PushTransform`/`Translate`/`PopTransform`,
  `PushOpacity`/`PopOpacity`, `Append(list | dx, dy)`, `Build()`, `Clear()`, `CommandCount()`.
  `Saturn.Combine([lists])` joins lists into one, bottom to top.

## The UI draw list

The one format Saturn, SaturnUI, ImGui and Clay produce and `drawUI` (graphics) draws. It is plain data, so any
renderer, in any language the runtime hosts, can draw it:

```text
{"version": 1, "commands": float[], "strings": string[], "images": [values], "vertices": float[], "indices": int[]}
```

`commands` is a stream of an op code followed by its arguments. Coordinates are logical pixels, colors 0..255.

| Op | Code | Arguments |
| --- | --- | --- |
| rect | 1 | x y w h, radii tl tr br bl, r g b a |
| border | 2 | x y w h, radii, widths left top right bottom, r g b a (inside the box) |
| shadow | 3 | x y w h, radii, blur spread, r g b a (the box already offset and spread) |
| text | 4 | x y w h (the glyph box of one line), string index, font family string index or -1, size, weight, italic (0/1), ascent, r g b a |
| image | 5 | x y w h, image index, u0 v0 u1 v1, radii, tint r g b a |
| clip push | 6 | x y w h, radii (intersects the current clip) |
| clip pop | 7 | |
| transform push | 8 | a b c d e f (x' = a x + c y + e, y' = b x + d y + f, applied to what follows) |
| transform pop | 9 | |
| mesh | 10 | first vertex, vertex count, first index, index count, texture: an id of op 11 (>= 0), -1 none, or -(image index) - 2 |
| texture | 11 | id, width, height, image index of RGBA bytes (-1 frees the texture), x y w h of the region the bytes cover |
| line | 12 | x1 y1 x2 y2, width, r g b a |
| gradient | 13 | x y w h, radii, x1 y1 x2 y2 (the color line), r g b a (from), r g b a (to) |
| opacity push | 14 | alpha 0..1 (multiplies everything until the pop) |
| opacity pop | 15 | |

Vertices hold `x y u v r g b a` per vertex; a mesh's indices count from its first vertex. Renderers keep textures by
id across lists (ImGui sends its font atlas once and then only updated regions); ImGui's ids start at 1048576.

## Widgets (`SaturnUI`)

```aqua
var UI = import("SaturnUI");
struct CounterState { count }
var counter = UI.Stateful(
    init: function() { return CounterState(count: 0); },
    build: function(state, context) {
        return UI.Column(gap: 12, crossAxis: "center", style: {"padding": 24}, children: [
            UI.Text(text: "Taps: " + String.From(state.count), style: {"fontSize": 22, "fontWeight": 700}),
            UI.Button(label: "+1", onPress: function() { state.count += 1; }),
            UI.Button(label: "Reset", variant: "outline", onPress: function() { state.count = 0; })
        ]);
    });
UI.RunApp(counter, {"title": "Counter", "theme": UI.Theme(mode: "dark")});
```

Widgets are structs: build them with named fields, nest them with `child:` and `children:` (null children are skipped
and nested arrays flatten, so `if` expressions and loops build lists). A surface keeps an element per widget,
matched by type and `key` across rebuilds, with its state and one Saturn node. Every event handler that runs rebuilds
the surface afterwards, so there is no `setState`; `context.rebuild()` covers timers and asynchronous results.

| Group | Widgets |
| --- | --- |
| layout | `Row`, `Column` (`gap`, `mainAxis`, `crossAxis`: `start`, `center`, `end`, `stretch`, `between`, `around`, `evenly`; `wrap`), `Stack` + `Positioned`, `Expanded`, `Flexible`, `Spacer`, `Padding`, `Center`, `Align` (`topLeft` .. `bottomRight`), `SizedBox`, `AspectRatio`, `Wrap`, `Grid` (`columns`), `ScrollView`, `ListView` (`itemCount`, `itemBuilder`; virtualized with `itemExtent`), `Box`, `Card`, `Divider` |
| content | `Text` (`maxLines`, `ellipsis`, `align`; a plain string or number is a Text), `RichText` + `Span`, `Image` (`fit`, `radius`), `Icon` (`check`, `close`, `plus`, `minus`, `chevronLeft/Right/Up/Down`, `menu`, `search`, `star`, `heart`, `home`, `settings`, `arrowLeft/Right`, `dot`, `circle`, `info`, `user`), `CustomPaint` (`paint(painter, w, h)`) |
| controls | `Button` (`label`, `icon`, `onPress`, `variant`: `outline`, `text`, `tonal`, `danger`; `disabled`), `IconButton`, `Checkbox`, `Switch`, `Radio` (`value`, `group`), `Slider` (`min`, `max`, `step`), `ProgressBar`, `TextField` (`placeholder`, `obscure`, `onSubmit`), `Dropdown` (`options`), `Tabs` (`labels`, `index`) |
| overlays | `Dialog` (`open`, `title`, `content`, `actions`, `onDismiss`), `Tooltip` (`message`); dropdown menus |
| behavior | `Gesture` (`onTap`, `onDoubleTap`, `onLongPress`, `onHover`, `onPress`, `onRelease`, `onDragStart`, `onDrag`, `onDragEnd`, `cursor`), the same handlers on `Box`, `onTap` on `Card` |
| structure | `Stateful` (`init`, `build(state, context)`, `dispose`), `Builder` (`build(context)`), `Themed` (a subtree with its own theme), `Immediate` (`paint(x, y, w, h)` returns another library's draw list; `feed(event)` gets its events in its own coordinates) |

Your own widgets are functions that return widgets.

**Styling** applies, in order: the theme's style sheet (every built-in widget and its states), the theme's `sheet`,
the app's `sheet` (`RunApp` / `Surface` options), each with the widget's type (`"Button"`), its variant
(`"Button.outline"`) and its classes (`class: "primary big"` → `"primary"`, `"Button.primary"`), breakpoint entries
(`"@narrow card"` under 600 wide, `"@medium ..."`, `"@wide ..."` from 1024) and state entries (`"Button:hover"`,
`:pressed`, `:focused`, `:disabled`, `:checked`, `:selected`); then parent data (`Expanded`, `Positioned`); then the
widget's inline `style` (a hash or `UI.Style(...)`). Styles take every key of `SetStyle`, including `transition`.
`UI.Theme(mode: "dark", primary: "#ff7a59", radius: 12, ...)` fills what it leaves out for its mode
(`UI.ResolveTheme`), and `surface.SetTheme(theme)` switches at run time.

**Surfaces**: `UI.Surface(width, height, root, options)` (options `theme`, `sheet`, `transparent: true` for a HUD or a bar over other content) → `Frame(timeMs)` (rebuild, layout, scrolling),
`Paint()` (the draw list), `Feed(event)` (Interaction events, or hashes with `type`, `x`, `y`, `button`, `dx`, `dy`,
`key`, `action`, `text`; returns whether the surface used it), `Wants(x, y)`, `Resize`, `SetRoot`, `SetTheme`,
`Rebuild`, `Cursor()` (for `session.SetCursor`), `ImeRect()`, `NeedsFrame()`. A surface draws nothing and owns no
window: put its list on a window, a canvas, a texture on a 3D object (feed it events mapped onto the object), or save
it. `UI.RunApp(root, options)` does the window, input and drawing for the common case.

**Layers** mix retained and immediate interfaces: `UI.Layers([UI.SurfaceLayer(surface), UI.ImGuiLayer(context,
build, embed), UI.ClayLayer(layout, build)])` (bottom to top) → `Frame(width, height, time)`, `Paint()` (one combined
list), `Feed(event)`, `Cursor()`; `UI.RunLayers(stack, options)` runs it in a window. The pointer goes to the top layer
that wants the point (a widget that paints or takes input, an ImGui window, a visible Clay element); the layer a press
lands on keeps the pointer until the release and takes the keyboard; layers below see the pointer leave. Transparent
layout boxes let the pointer through, so a HUD never blocks the scene under it. Embedding works both ways without
textures: `UI.Immediate(...)` hosts ImGui or Clay inside a Saturn layout, `ImGui.Embed(id, w, h)` with
`context.Render(embed)` and Clay elements with `"embed": f(x, y, w, h)` host a Saturn surface's list.

## Tests

`tests/node_api` (the Yoga API and its errors), `style_paint` (styles, text, painting, hit testing, painters),
`inherit_transition`, `widgets` (SaturnUI reconciliation and every control's input), `layers` (mixing with ImGui and
Clay). They need no window and use the built-in text measurer, so they give the same output on every target.
