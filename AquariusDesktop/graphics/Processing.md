# Processing for Aquarius

`匯入("Processing")` provides a creative-coding library backed by the desktop
host's wgpu backend. It generates geometry, WGSL pipelines, GPU textures and
render targets. GLFW supplies windows/input; wgpu owns rendering/presentation.
Importing the module, doing math, creating vectors, constructing retained shapes,
editing PImages and using color functions do not initialize GLFW.

The library follows the names and core conventions in the
[Processing reference](https://processing.org/reference/). Aquarius programs use
module members and Chinese language keywords; existing Java Processing sketches
need their syntax adapted. This is a drawing-library implementation, not the
Processing Java runtime or its third-party ecosystem.

## Start a sketch

Build `native/build.ps1` first, then build the desktop project. The updated native
bridge is required for Unicode character input, scrolling and window sizing.

```text
變數 p = 匯入("Processing");
變數 設定 = 函式() { p.size(800, 600, p.P2D, "My sketch"); };
變數 繪製 = 函式() {
    p.background(24, 30, 48);
    p.noStroke();
    p.fill(50, 220, 190);
    p.circle(p.mouseX, p.mouseY, 60);
};
p.run(設定, 繪製);
```

`run(setup, draw)` invokes zero-argument Aqua closures and owns the event loop.
Setup, draw and event callbacks execute their compiled bodies through
`CompiledEvaluator.Invoke`; captured variables persist across callback invocations.
Call `size(width,height[,renderer[,title]])` in setup, or
`fullScreen([renderer[,title]])` to use the primary monitor. Renderers are `P2D`
and `P3D`. Both use wgpu; P2D has a top-left origin and positive Y downwards.
P3D starts with Processing-style screen coordinates and a perspective camera.
Angles are radians. Default styles are white fill, black stroke, weight 1,
`rectMode(CORNER)`, `ellipseMode(CENTER)`, `imageMode(CORNER)` and RGB 0–255.

`run` releases the window and all GPU resources on exit and on callback errors.
For a manual loop, call `size`, then `beginFrame()` / your drawing / `endFrame()`
and finish with `close()`. Manual loops must manage their own frame counters,
throttling and stopping conditions. A host can start another sketch after close;
images/shaders/graphics from the previous sketch are no longer usable.
Do not terminate or replace the underlying GLFW context while a sketch is active.

## API

Per-canvas drawing, style, transforms, lights, image display, pixels, text, shader
and retained-shape functions also exist on a `PGraphics` object. Lifecycle/input,
math, `createGraphics`, `createImage`, `loadImage` and `createFont` use the main
Processing module. Constants live on the Processing module.
Optional arguments are shown in brackets.

| Area | Functions |
| --- | --- |
| Sketch control | `size(w,h[,renderer[,title]])`, `fullScreen([renderer[,title]])`, `run(setup,draw)`, `beginFrame()`, `endFrame()`, `frameRate(fps)`, `noLoop()`, `loop()`, `redraw()`, `exit()`, `close()`, `millis()` |
| Input | `on(event,callback)`, `keyDown(GLFWkeyCode)`, `cursor()`, `noCursor()` |
| Primitives | `point(x,y[,z])`, `line(x1,y1,x2,y2)` / six-coordinate 3D overload, `triangle(...)`, `quad(...)`, `rect(x,y,w,h[,radius])`, `square(x,y,size)`, `ellipse(x,y,w,h)`, `circle(x,y,d)`, `arc(x,y,w,h,start,stop[,mode])` |
| Shape construction | `beginShape([kind])`, `vertex(x,y[,z][,u,v])`, `normal(x,y,z)`, `texture(image)`, `textureMode(mode)`, `beginContour()`, `endContour()`, `endShape([CLOSE])` |
| Curves | `bezier(...)`, `curve(...)` (8 or 12 coordinates), `bezierVertex(...)` (6 or 9), `quadraticVertex(...)` (4 or 6), `curveVertex(x,y[,z])`, `bezierDetail(n)`, `curveDetail(n)`, `curveTightness(value)` |
| Curve math | `bezierPoint(a,b,c,d,t)`, `bezierTangent(a,b,c,d,t)`, `curvePoint(a,b,c,d,t)`, `curveTangent(a,b,c,d,t)` |
| Style | `fill(...)`, `noFill()`, `stroke(...)`, `noStroke()`, `strokeWeight(n)`, `strokeCap(mode)`, `strokeJoin(mode)`, `rectMode(mode)`, `ellipseMode(mode)`, `smooth([samples])`, `noSmooth()` |
| Color | `colorMode(mode[,max])` / `colorMode(mode,max1,max2,max3[,maxAlpha])`, `color(...)`, `red(c)`, `green(c)`, `blue(c)`, `alpha(c)`, `hue(c)`, `saturation(c)`, `brightness(c)`, `lerpColor(c1,c2,t)` |
| Canvas | `background(...)`, `clear()`, `clip(x,y,w,h)`, `noClip()`, `blendMode(mode)` |
| Transforms | `translate(x,y[,z])`, `rotate(radians)`, `rotateX/Y/Z(radians)`, `scale(s)` / `scale(x,y[,z])`, `shearX/Y(radians)`, `applyMatrix(columnMajor16Array)`, `getMatrix()`, `resetMatrix()` |
| State stacks | `pushMatrix()`, `popMatrix()`, `pushStyle()`, `popStyle()`, `push()` / `pop()` (matrix and style together) |
| Coordinate conversion | `modelX/Y/Z(x,y[,z])`, `screenX/Y/Z(x,y[,z])` |
| 3D | `box(size)` / `box(w,h,d)`, `sphere(radius)`, `sphereDetail(u[,v])`, `camera()` / `camera(eyeX,eyeY,eyeZ,targetX,targetY,targetZ,upX,upY,upZ)`, `perspective()` / `perspective(fov,aspect,near,far)`, `ortho()` / `ortho(left,right,bottom,top[,near,far])` |
| Lighting | `lights()`, `noLights()`, `ambientLight(r,g,b)`, `directionalLight(r,g,b,nx,ny,nz)`, `pointLight(r,g,b,x,y,z)`, `spotLight(r,g,b,x,y,z,nx,ny,nz,angle,concentration)`, `lightFalloff(constant,linear,quadratic)`, `lightSpecular(r,g,b)` |
| Material | `ambient(...)`, `specular(...)`, `emissive(...)`, `shininess(n)` |
| Images | `loadImage(path)`, `createImage(w,h[,ARGB or RGB])`, `image(img,x,y[,w,h])`, `imageMode(mode)`, `tint(...)`, `noTint()`, `copy(img,sx,sy,sw,sh,dx,dy,dw,dh)` |
| Pixels | `loadPixels()`, `updatePixels()`, `get()` / `get(x,y)` / `get(x,y,w,h)`, `set(x,y,colorOrImage)`, `filter(mode[,parameter])`, `save(path)`, `saveFrame(pathWithHashes)` |
| Text | `createFont(systemName,size)`, `textFont(fontOrName[,size])`, `textSize(size)`, `textLeading(n)`, `textAlign(horizontal[,vertical])`, `text(value,x,y[,z])` / `text(value,x,y,w,h)`, `textWidth(value)`, `textAscent()`, `textDescent()` |
| Reusable graphics | `createGraphics(w,h[,P2D or P3D])`, `createShape()`, `shape(pshape[,x,y])` |
| WGSL | `createShader(vertexSource,fragmentSource)`, `loadShader(fragmentPath[,vertexPath])`, `shader(pshader)`, `resetShader()` |

`fill`, `stroke`, `background`, `tint`, `color`, and material colors accept
grayscale, grayscale/alpha, RGB or HSB triples, quadruples with alpha, or packed
ARGB colors returned by `color()`. Channel readers use the current color maxima.
Packed colors use exactly represented unsigned numeric values, not signed Java
integers. Alpha blending and transparent offscreen canvases are supported.

Polygon boundaries must be simple and planar. Concave polygons and multiple
holes work; put each hole inside `beginContour` / `endContour`. Contours need not
use an opposite winding. Shape modes are `POINTS`, `LINES`, `TRIANGLES`,
`TRIANGLE_STRIP`, `TRIANGLE_FAN`, `QUADS`, `QUAD_STRIP`, `POLYGON`.
`textureMode(NORMAL)` uses 0..1 UVs; `IMAGE` uses image pixel coordinates.
Do not draw primitives or change transforms between `beginShape` and `endShape`.

Modes: `CORNER`, `CORNERS`, `CENTER`, `RADIUS`; arcs: `OPEN`, `CHORD`, `PIE`;
caps: `ROUND`, `SQUARE`, `PROJECT`; joins: `MITER`, `ROUND`, `BEVEL`;
blend modes: `BLEND`, `ADD`, `MULTIPLY`, `SCREEN`, `REPLACE`.
P2D strokes are tessellated geometry; P3D strokes are projected to constant
screen width. Miter joins have a four-radius limit. Geometry behind the camera
is clipped; 3D strokes crossing the eye plane are omitted.

## Animation and events

Register `on("error", callback)` to recover an input or resize callback failure.
The zero-argument handler reads `errorEvent` and `errorMessage`. Both desktop and
browser hosts finish interrupted offscreen drawing before calling the handler.
Unhandled failures and failures in the handler still end execution; browser
cancellation always propagates. Setup and draw failures remain fatal.

At frame boundaries both hosts reclaim images and offscreen canvases that are no
longer reachable from language scopes, active calls or callbacks. Keep resources
in an Aquarius variable, container or closure while using them. Extracted native
methods also retain their owning resource.

Each frame resets the main model matrix and its matrix stack, and clears lights.
Fill/stroke/text/color settings persist. In P3D, call `lights()` or add lights
inside draw. There can be eight directional/point/spot lights per canvas.
`lights()` installs gray ambient light and a gray directional light. Light
positions/directions are captured with the current model transform.

Read `width`, `height`, `pixelWidth`, `pixelHeight`, `frameCount`, `actualFrameRate`,
`mouseX`, `mouseY`, `pmouseX`, `pmouseY`, `mouseButton`, `isMousePressed`,
`key`, `keyCode`, `isKeyPressed`, `wheelCount` from the module. Main `frameCount`
starts at 1 in the first draw. `actualFrameRate` is distinct from the
`frameRate(fps)` setter because module fields and functions share a namespace.
Framebuffer sizes follow the display's pixel density automatically. Drawing
coordinates are logical pixels; `pixels` contains physical framebuffer pixels.
Resizing the window or changing display density recreates the render attachments,
updates these fields before `windowResized`, and requests a draw even under
`noLoop()`. Minimized windows skip rendering and redraw when restored. Text
rasterization follows the current transform and framebuffer density.
`resize(width,height)` requests a new logical window size; the next frame applies it.

Events: `mousePressed`, `mouseReleased`, `mouseClicked`, `mouseMoved`,
`mouseDragged`, `mouseWheel`, `keyPressed`, `keyReleased`, `keyTyped`,
`windowResized`. Callbacks take zero parameters and read the fields above:

```text
p.on("keyPressed", 函式() {
    如果 (p.keyCode == p.SPACE) { p.noLoop(); }
});
p.on("mousePressed", 函式() { p.redraw(); });
```

Esc exits `run()`. Key codes use GLFW, so A–Z are 65–90; `ESC` is 256,
`SPACE` is 32. Mouse buttons are 0/1/2 (`LEFT`/`RIGHT`/`CENTER`). Key/button
transitions are polled once per frame; very short taps between polls can be missed.
Unicode typed characters and wheel deltas are queued by the native bridge.
For editable multilingual text, use `startTextInput()`, `stopTextInput()`, and
`textInputRect(x,y,w,h)` with `textInput` and composition callbacks. See the
[text input guide](TextInput.md) for fields, Windows IME support and platform
limitations. While text input is enabled, automatic Esc exit is disabled.
`noLoop()` keeps polling input and drawing after `redraw()`; `exit()` ends the loop.

## PImage, PGraphics, PShape, PShader

PGraphics supports `resize(width,height[,pixelWidth,pixelHeight])` outside
`beginDraw()` / `endDraw()`. The first two dimensions set drawing coordinates;
the optional pair sets the backing texture resolution independently. Its
`width`/`height` and `pixelWidth`/`pixelHeight` fields expose both sizes.
Changing the backing resolution clears the texture; draw it again and call
`loadPixels()` before editing its new pixel array. For example, a 240×240
canvas displayed at twice its original size can use `pg.resize(240,240,480,480)`.
Drawing it with `image(pg,x,y)` still uses its logical 240×240 size.

`PImage` is a module object with `width`, `height`, and (after `loadPixels()`)
`pixels`. Methods: `loadPixels`, `updatePixels`, `get` (all three overloads),
`set(x,y,color)`, `save`, `filter`, `mask(image)`, `resize(w,h)`, and
`copy(src,sx,sy,sw,sh,dx,dy,dw,dh)`. A zero resize dimension preserves aspect ratio;
resize/copy currently use nearest-neighbor sampling. Out-of-bounds reads return
transparent black; writes outside the image are ignored. `RGB` images start
opaque black; `ARGB` images start transparent. Filter modes are `GRAY`, `INVERT`,
`THRESHOLD` (default 0.5), `POSTERIZE` (2–255 levels), `OPAQUE`, and `BLUR`
(separable box blur, radius 1–32). Masks use the source's blue channel as alpha.

```text
變數 圖 = p.createImage(32, 32, p.ARGB);
圖.loadPixels();
變數 像素 = 圖.pixels;
像素[0] = p.color(255, 80, 40);
圖.updatePixels();
p.image(圖, 20, 20);
```

`get(x,y)` returns a packed ARGB color; other get overloads return a PImage.
Canvas `loadPixels()` exports pixels top-to-bottom. Save inside draw, before
buffer swapping, to capture the completed current frame. Saving supports PNG (RGBA)
and binary PPM (RGB). `saveFrame("frame-####.png")` replaces the consecutive
hashes with the current frame number. Image decoding uses the existing stb_image
backend (including PNG, JPEG, BMP, TGA, PNM). Dimensions are limited to 8192 per
axis, with at most 16 million pixels per image.

`PGraphics` exposes drawing functions plus `beginDraw()` and `endDraw()`,
and can be passed to `image`, `texture`, `copy` and `mask`. Its render target
has a texture, depth buffer and stencil buffer. Its logical/physical sizes match.
Call `endDraw` before compositing it. Each beginDraw resets its model matrix;
its other settings persist. A canvas cannot be drawn into itself.
`get()`, `loadPixels()`, `save()` and `saveFrame()` can read a completed
PGraphics after `endDraw()` without starting another drawing session. `get()`
returns an independent snapshot; subsequent drawing does not change it.
Drawing and pixel updates still require `beginDraw()` / `endDraw()`.

`PShape` exposes `beginShape`, `vertex`, `endShape`, `fill`, `stroke`, `noFill`,
`noStroke`, `strokeWeight`, `setFill(packedColor)`, `setVertex(index,x,y[,z])`,
`getVertex(index)`, `getVertexCount`, `translate`, `rotate`, `scale`,
`resetMatrix`, `addChild(shape)`. Groups reject cyclic child relationships.
This retained subset supports vertex primitives and concave polygons;
use immediate shapes for curves, contours and textures.

`PShader.set(name,value...)` accepts scalar floats, 2–4 component vectors,
arrays of 1–4 values, or a 16-element matrix. `setInt(name,value)` sets integer
fields. Custom shaders now use WGSL with `vs_main` and `fs_main` entry points.
Declare custom fields in `UserUniforms` at group 1/binding 0; the renderer's
shared WGSL header supplies `VertexInput`, `VertexOutput`, `processing`,
`surface`, and `surfaceSampler`. `loadShader(fragmentPath)` uses the default
WGSL vertex shader. GLSL shaders need conversion; the raw GL library remains
available. Shader compile errors include wgpu's validation diagnostic.
See [the wgpu guide](WGPU.md#processing-migration-and-shaders) for the shader
interface, uniform types/alignment, and depth convention. The updated showcase
contains a complete custom WGSL shader.
## Math and PVector

Constants: `PI`, `TWO_PI`, `TAU`, `HALF_PI`, `QUARTER_PI`.
Functions: `abs`, `ceil`, `floor`, `round`, `sqrt`, `sq`, `exp`, `log`, `pow`,
`min`, `max`, `constrain`, `lerp`, `norm`, `map`, `dist` (2D/3D), `mag` (2D/3D),
`sin`, `cos`, `tan`, `asin`, `acos`, `atan`, `atan2`, `radians`, `degrees`,
`random(max)` / `random(min,max)` / `random(array)`, `randomGaussian`,
`randomSeed`, `noise` (1–3 coordinates), `noiseSeed`, `noiseDetail(octaves[,falloff])`.
Noise is seeded, continuous, normalized octave Perlin noise; it does not reproduce
Processing's exact numeric sequence. Random seeds are deterministic in this
.NET runtime, and do not match Java's RNG sequence.

`createVector(x,y[,z])` (alias `PVector`) creates a module with `x`, `y`, `z`.
Methods: `set(x,y[,z])`, `add`, `sub`, `mult`, `div`, `normalize`, `setMag`,
`limit`, `rotate`, `lerp`, `copy`, `array`, `mag`, `magSq`, `heading`, `dot`,
`cross`, `dist`, `angleBetween`. Mutating methods return the vector; add/sub/dot/
cross/dist/angleBetween accept vectors or two-/three-element numeric arrays.
Use `set` to change components; vector fields are readouts.

## Current differences and limits

The core drawing and creative-coding features above are implemented. Processing
features outside this API include SVG/OBJ loading, `.vlw` fonts, PDF/SVG export,
PShape curves/textures/contours, asynchronous image requests, shader filter passes,
per-pixel blend functions, advanced stroke clipping at the camera plane, and
Processing's Java data/network/media/third-party libraries. Multiple windows and
multiple monitor selection are outside the current Aquarius GLFW backend.

Windows text uses installed system fonts through GDI and supports Unicode/CJK.
Other platforms currently use an embedded ASCII bitmap fallback with uppercase
letter forms and placeholder boxes for unavailable glyphs. Text ascent/descent
use approximate 80%/20% metrics; boxed text wraps on spaces and clips by line count.
The main context requests 4x MSAA; smooth/noSmooth toggle it rather than changing
the context's sample count, and offscreen targets currently are single-sample.
3D uses per-fragment lighting and a depth buffer; it does not implement Java
Processing's exact light equations or sorting of translucent 3D surfaces.

## Showcase and verification

The [color mapping lab](../examples/color_mapping/README.md) is a complete
Aquarius UI example using Processing shapes, text, PImage pixels, `map`,
`constrain`, `pow`, `lerpColor`, and input callbacks. It includes four palettes,
three scalar fields, range/gamma sliders, stepped colors, a pixel probe, and PNG
export. It redraws on input with `noLoop()` / `redraw()` and scales both the layout
and pointer coordinates when resized.

```powershell
dotnet run --project AquariusDesktop -- AquariusDesktop/examples/color_mapping/main.aqua
```

Run the [showcase](../examples/processing_showcase/main.aqua):

```powershell
dotnet run --project AquariusDesktop -- AquariusDesktop/examples/processing_showcase/main.aqua
```

Space pauses motion, the mouse attracts particles, S saves a PNG beside the script,
and Esc exits. For repeatable finite captures:

```powershell
$env:AQUARIUS_GRAPHICS_FRAMES = '3'
$env:AQUARIUS_GRAPHICS_CAPTURE = "$PWD/showcase.png"
dotnet run --project AquariusDesktop -- AquariusDesktop/examples/processing_showcase/main.aqua
```

`AQUARIUS_GRAPHICS_FRAMES=0` (or unset) means no limit. Clear these environment
variables afterwards to return to interactive mode. Headless tests exercise color,
geometry, transforms, images, vectors, deterministic noise, arguments and parsing.
Set `AQUARIUS_WGPU_TESTS=1` to also validate exact framebuffer pixels, contour
holes, offscreen orientation/alpha, the showcase's shaders/text/lighting, callback
closure state and resource cleanup. Raw OpenGL tests still use `AQUARIUS_OPENGL_TESTS=1`.
