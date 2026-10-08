# Aquarius Web Compiler

The preferred web command now consumes a compiled bottle:

```cmd
aqua build main.aqua lib\tools.aqua --root . --assets assets -o app.bottle
aqua build app.bottle --target web -o dist\web
```

The standalone command also accepts `AquariusWebCompiler app.bottle output-directory
[entry.rius]`. Original sources and resource folders are unnecessary. Existing
source-based commands below remain compatible through the shared package pipeline.
See [the unified CLI guide](../AquariusCli/README.md).

The compiler packages Aquarius source as the core VM's bytecode, a JavaScript stack VM, WebGPU graphics, and Jolt WebAssembly. Output is a static website with no application server and no CDN dependency. Script sources are compiled by `VmCompiler`; the browser does not parse Aquarius or translate its source into JavaScript.

Build from the solution directory:

```powershell
npm ci --prefix AquariusWebCompiler
npm run --prefix AquariusWebCompiler prepare:browser
dotnet build AquariusLang.sln -m:1
dotnet run --no-build --project AquariusWebCompiler -- examples .web-build/examples
dotnet run --no-build --project AquariusWebCompiler -- C:\OfficialProjects\AquariusLang_MarbleRun_3D .web-build/marble main.aqua
```

Serve the generated folder over localhost or HTTPS, for example `python -m http.server 8080 --directory .web-build`, then open `/examples/` or `/marble/`. WebGPU requires a supported browser and GPU adapter. The generated site automatically runs the bottle's selected entry and the game fills the browser viewport without runner controls or scrollbars. Console-only programs display their output; game output goes to the browser console, with errors displayed over the game. Imports resolve inside the bundled virtual filesystem; images and shaders travel with the package. PNG saves become browser downloads.

The browser host samples the viewport and display density at the start of each
frame. `width`/`height` remain logical layout/input coordinates, while
`pixelWidth`/`pixelHeight` specify physical render-target pixels. Targets and
presentation update before `windowResized`; resizing also redraws `noLoop()`
sketches. Text rasterization and clipping follow physical density. Hidden
surfaces keep their logical size and resume drawing when restored. Desktop uses
the same core `GraphicsSurfaceSize` contract with GLFW measurements. Window/DOM
details stay in the hosts and GPU presentation stays in the backends.

The browser owns its viewport: `size()` and `fullScreen()` create a viewport-sized
main surface, and `resize()` cannot change the browser window. Offscreen
`createGraphics()` canvases retain explicit logical and physical sizes, including
`resize(w,h,pixelWidth,pixelHeight)`. For development and tests,
`?autorun=0` disables startup and `window.aquarius.run(entry,frames)`/`stop()` remain
available in the developer console; ordinary page loads use the packaged entry.

The marble-run source works unchanged. `smoke.aqua` exercises all eleven gameplay stages, including steering, braking, pause, jumping, checkpoints, respawning, finishing, restart, camera changes, and resizing. `main.aqua` is the playable entry. Keyboard, pointer and wheel events drive the same callbacks as desktop. Browser text input uses DOM composition events and grapheme-aware deletion.

`scripts/build-web.ps1` builds both websites in one step. It defaults to the included MIT-licensed marble fixture; pass `-MarbleSource C:\OfficialProjects\AquariusLang_MarbleRun_3D` to build the live project.

The existing OpenGL cube runs through a WebGL2 compatibility adapter. Processing and WGPU rendering, including marble-run's custom WGSL, use WebGPU. `execFile` cannot start desktop processes from a static website; the existing process example checks the OS and therefore never calls it on the browser. General desktop-only native OpenGL calls outside the supported example subset are not a portable browser API. The browser Processing adapter covers the current examples; the full desktop library includes additional functions that have not yet been ported.

Validation:

```powershell
npm test --prefix AquariusWebCompiler
$env:AQUARIUS_WGPU_TESTS = "1"
dotnet test AquariusLang.sln -m:1
node AquariusWebCompiler/tests/browser-smoke.mjs
node AquariusWebCompiler/tests/browser-resize.mjs
```

The browser smoke runner expects the example and marble websites built under `.web-build`, uses installed Chrome (or `AQUARIUS_BROWSER=msedge`), checks real compute/readback, rendered color variation, OpenGL errors and the marble assertions, and writes `browser-results.json` and marble screenshots. Its WebGPU flag is for test execution. Unit tests compare JavaScript execution with core execution of the same compiler-produced instructions. Vendor versions and licenses are pinned by `package-lock.json`; rerun `prepare:browser` before rebuilding when dependencies change.
# Application integration

See [portable application libraries](../docs/application-libraries.md) for scoped
file resources, persistent storage, codecs, clipboard, input and browser restrictions.
Run `npm run prepare:browser` after dependency installation before compiling a website.
