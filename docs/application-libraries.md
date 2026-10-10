# Application libraries

Core contracts, validation, ownership, document formats, editing and registration live
in `AquariusCore/application`. Desktop adapters live in `AquariusDesktop/application`;
browser adapters live in `AquariusWebCompiler/browser/application-*.mjs`. Core's
`portable.mjs` supplies shared browser behavior without DOM or storage dependencies.
Adapters attach to existing GLFW windows/canvases. Rendering, shaping, presentation,
IME queues and package loading retain their existing owners.

## API reference

Import modules with `匯入("Files")` or `import("Files")`. Members have English and Traditional Chinese aliases;
`ApplicationCatalog` publishes their argument counts to both hosts and editor tooling.
Records have lower camel case fields; nested data uses string-keyed hashes. Binary
values are arrays of integers in 0..255. Failures never report successful no-ops.

| Module/object | Members |
|---|---|
| `Files` | `Capabilities()`, `Resolve(location)`, `Stat(resource)`, `ReadBytes(resource)`, `ReadText(resource,encoding)`, `SaveBytes(resource,bytes[,requireAtomic])`, `SaveText(resource,text,encoding[,requireAtomic])` |
| `Files` | `Open(resource,mode)`, `Enumerate(directory)`, `CreateDirectory(directory)`, `Delete(resource[,recursive])`, `Copy(source,destination[,overwrite])`, `Move(source,destination[,overwrite])`, `Temporary(isDirectory)` |
| stream | `Read(count)`, `Write(bytes)`, `Seek(offset,"Begin"/"Current"/"End")`, `Flush()`, `Close()`; `canRead`, `canWrite`, `canSeek` |
| `Paths` | `Normalize(path)`, `Join(parts)`, `Name(path)`, `Extension(path)`, `Parent(path)`, `Relative(fromDirectory,target)` |
| `Images` | `Formats()`, `Inspect(bytes)`, `Create(width,height,rgbaBytes)`, `Decode(bytes,frame)`, `Load(resource,frame)`, `Encode(image,format,options)`, `Save(resource,image,format,options)` |
| image | `width`, `height`, `pixelFormat`, `alpha`, `metadata`, `Pixels()` |
| `Clipboard` | `Capabilities()`, `Formats()`, `ReadText()`, `ReadImage()`, `WriteText(text)`, `WriteImage(image[,textRepresentation])` |
| `Window` | `Attach(glfwWindow)`, `Current()` for Processing, `FromProcessingImage(image)`, `ToProcessingImage(image)` |
| window integration | `Capabilities()`, `Poll()`, `SetTitle(title)`, `SetCursor(shape)`, `SetCustomCursor(image,x,y)`, `CapturePointer(id)`, `ReleasePointer(id)`, `ResolveClose("Accept"/"Defer"/"Cancel")` |
| `TextEdit` | `Create(text)`, `Convert(text,position,fromUnit,toUnit)` |
| editor | `State()`, `Select(anchor,caret)`, `Insert(text)`, `Backspace()`, `Delete()`, `Move(direction,unit,extendSelection)`, `Convert(position,fromUnit,toUnit)` |
| editor | `Composition(text,cursor[,cursorUnit])`, `CancelComposition()`, `Commit(text)`, `Copy()`, `Cut()`, `Paste()`, `Shortcut(logicalKey,modifierMask)` |
| `Fonts` | `Enumerate()`, `Available(family)`, `Measure(text,family,size)`, `Selection(text,startUtf16,endUtf16,family,size)` |
| `Serialization` | `Json(value)`, `ParseJson(text)`, `Binary(value,schemaVersion)`, `ParseBinary(bytes)`, `Compress(bytes,format[,level])`, `Decompress(bytes,format)`, `Archive(resourceHash)`, `ReadArchive(bytes)` |
| `Storage` | `SettingsDirectory()`, `DataDirectory()`, `Read(key)`, `Write(key,bytes)`, `Delete(key)`, `GetPreference(key)`, `SetPreference(key,value)`, `Recent()`, `Remember(resource)` |
| `Application` | `Capabilities()`, `OpenFiles(options)`, `SaveFile(options)`, `PickDirectory()`, `LaunchFiles()`, `AcquireImage()`, `Print(image)`, `Accessibility(nodes)`, `RegisterFileAssociation(options)` |
| `Tasks` | `CreateCancellation()`, `UseCancellation(token)`; token has `Cancel()`, `IsCancelled()`, `Dispose()` |

Resources are opaque, scoped handles with `provider`, `id`, `name`, `kind` (0 file,
1 directory, 2 asset), and optional `persistentIdentity`. An ID need not be an OS path.
`Stat` is authoritative for existence/kind; resolving does not create anything. Reopen
recent descriptors through their provider; descriptors grant no new permissions.
Browser-selected `File`s are read-only and scoped to the run. Packaged assets remain
separate, with unchanged package validation and loading.

Save operations return whether replacement was atomic. Desktop stages a neighboring
temporary file, flushes it, then replaces/renames it. Browser IndexedDB commits a
transaction; selected writable handles use `createWritable` staging. Downloads return
false and reject `requireAtomic=true`. Atomic visibility does not guarantee power-loss
durability on every filesystem. Desktop parent directories must exist. Directory copy
requires a new destination and rejects desktop symbolic links. Browser persistent
directory copy/move uses one transaction.

Modes are `Read`, `Create`, `OpenWrite`, `Append`; offsets/counts are bytes and reads
may be short. Close is idempotent. Browser streams buffer bounded output and commit on
flush/close; cancelled runs discard unflushed output. Temporary resources expose
`Dispose()` and clean up at runtime disposal. Browser locations are `persistent:/name`,
`download:/name`, or `temporary:/lease-id/child` under a live temporary directory.
Strict, BOM-free write encodings are `utf-8`, `utf-16le`, `utf-16be`, `ascii`; reads retain
BOMs. Paths are lexical slash paths with drive/UNC roots. They do not authorize access
or resolve symlinks; drive-relative paths fail and relative paths require matching roots.

## Images, events and editing

PNG/JPEG/BMP/GIF/TIFF identification uses signatures. Desktop decoding uses pinned
Magick.NET Q8; browser codecs use pinned ImageMagick WASM in a cancellable worker with
coder, allocation, frame and time limits. Additional C# providers register independently
in `ImageCodecRegistry`. Output pixels are owned, top-down RGBA8, with explicit alpha
representation. Higher bit depth/alternate source models convert to this documented
representation. Metadata includes source pixel format, EXIF orientation 1..8, resolution
and indexed palettes where provided. Stored orientation remains unrotated. Frame/page
selection is mandatory and zero-based. Encoding accepts one explicitly selected image.

Options: `{quality:90,compression:6,background:16777215,allowMetadataLoss:false}`;
quality is 1..100 and compression 0..9. Quality controls JPEG; compression controls
PNG/TIFF (desktop levels map to runtime presets). JPEG/BMP require an explicit RGB
background for nonopaque alpha. GIF retains binary transparency and requires a
background for fractional alpha; palette conversion can reduce color precision.
GIF/BMP orientation loss and GIF physical resolution loss require `allowMetadataLoss:true`.
A single supplied resolution axis applies to both axes. PNG/TIFF preserve alpha;
output signatures are checked. BigTIFF and provider-unsupported variants fail explicitly.

`Window.Poll` drains events after the existing event pump. Types cover key press,
release/repeat, pointer movement/buttons, wheel, focus/visibility/activation, input
cancellation, capture loss, drops, close requests and lifecycle. Timestamps are monotonic
seconds; coordinates match existing logical surface units independent of density.
Wheel units are `pixel`, `line`, `page`, with positive Y downward. Keyboard identity
uses GLFW scan codes or DOM `code`. Modifier bits: Shift 1, Control 2, Alt 4, Super 8,
Caps Lock 16, Num Lock 32. Browser PointerEvent provides contact IDs, ordered/coalesced
samples and optional pen pressure/tilt/eraser information. Missing properties are null;
GLFW mouse ID is 0 and its current adapter has no touch/pen properties.

Cursor names: Arrow, Text, Crosshair, Hand, ResizeHorizontal, ResizeVertical,
ResizeDiagonal1, ResizeDiagonal2, Move, NotAllowed. Browser custom cursors are limited
to 128x128; native cursor availability depends on the window system. Attached desktop
close requests remain pending until accepted/deferred/cancelled. Browser Cancel enables
`beforeunload` confirmation, Accept removes it and Defer fails; the browser owns the
actual close decision and OS window size. Unattached GLFW windows retain legacy closure.

Editor positions use extended graphemes; conversion units are `Grapheme`, `Utf16`,
`UnicodeScalar`. Splitting a scalar/grapheme fails. Navigation units are `grapheme`,
`word` (letters/numbers/underscore with separator skipping), `line` (current start/end);
direction is -1 or 1. Composition remains separate, with a scalar cursor by default.
Pass `"Utf16"` when forwarding existing IME events. Browser Processing composition
cursors now match the documented desktop UTF-16 input contract. `Shortcut` declines
shortcuts during composition and never generates text. Append from `textInput` or
`keyTyped` once; cancel composition on focus loss. Measurements reuse existing
GDI/bitmap/canvas backends and their prefix advances. Complex bidirectional caret hit
testing remains limited by the existing backend; no new shaping framework is introduced.

## Documents and host capabilities

JSON supports null, Boolean, finite numbers, strings, arrays and string-keyed hashes.
Functions/native objects/cycles fail; numeric Aquarius type tags are not preserved and
duplicate keys use the last value. Binary layout is `AQD1`, little-endian uint32 schema,
JSON byte length, CRC32, then strict UTF-8 JSON. Schema starts at 1; decoding returns
`{schema,value}`. ZIP resources use relative slash names without empty/dot/parent
components; reads check sizes, duplicates and CRCs. Browser ZIP supports ordinary
stored/deflated entries; encrypted, multipart and ZIP64 archives fail.

Compression: `Gzip`, `Zlib`, raw `Deflate`, plus desktop `Brotli`; levels are `Optimal`,
`Fastest`, `SmallestSize`, `NoCompression`. One gzip member is supported; gzip/zlib
checksums and output sizes are validated. Raw deflate/Brotli have no container checksum;
use checksummed documents/archives for integrity. Defaults: 64 MiB encoded/expanded
bytes, 8192 dimensions, 16 Mi total frame pixels, 256 frames, 4096 archive entries,
JSON depth 64 and editor length 1 Mi UTF-16 units. Embedders can supply smaller limits.

Desktop storage uses roaming/local application directories; browsers use entry-scoped
IndexedDB subject to origin quota/eviction. Keys allow 1..128 ASCII letters/digits,
dot/underscore/hyphen. Language preferences use schema 1. Reusable C#
`Preferences(storage,schema)` supports `AddMigration` hooks advancing one version;
missing/newer schemas fail. `Remember` keeps 20 descriptors with durable identity
deduplication. `DesktopApplicationHost` permits application ID/storage root injection.

Picker options: `{title:"Open",multiple:true,suggestedName:"drawing.aqd",
filters:[{name:"Images",extensions:["png","jpg"]}]}`. Browser pickers require a focused
secure context/user gesture; use application interaction rather than startup autorun.
File System Access is used where available, otherwise file-input/download fallbacks.
Acquisition selects an image (mobile browsers may offer capture); scanner/camera SDKs
are not installed. Printing submits a fitted raster page through the native/browser
dialog; browser spool completion/cancellation cannot be confirmed. Accessibility nodes
(`id`, `role`, `label`, optional `value`, `disabled`) publish a read-only MSAA tree or
semantic DOM mirror, while application drawing and interactions retain ownership.

Associations accept `{extension:"aqd",applicationId:"MyApp",description:"My document",
executable:"C:/path/AquariusDesktop.exe",arguments:["C:/path/app.wasm","--documents"]}`.
Registration explicitly advertises a per-user Open With handler without overriding
default-app choices. `app.wasm --documents file1 file2` supplies `LaunchFiles()`;
embedders can pass launch files to the desktop host/runner. Browser launch resources
require an installed web app with file handlers and `launchQueue`. Static sites cannot
register OS associations or enforce single-instance activation.

| Capability | Windows | Linux/macOS | Browser |
|---|---|---|---|
| Files/streams/temp, settings | Yes | Yes | Scoped resources, buffered streams, IndexedDB |
| Five codecs/frame selection | Yes | Yes | Bundled WASM worker |
| Unicode clipboard | Win32 | Existing GLFW; initialize GLFW | Secure context/focus/permission |
| Image/multiple clipboard formats | PNG + DIBV5 + text | Adapter unavailable | PNG + text where permitted |
| Pickers | Win32 | Adapter unavailable | Feature checks/input/download fallback |
| Drops/events/cursors | GLFW | GLFW, cursor feature checks | DOM canvas events/cursors |
| Pointer capture | Win32 | Adapter unavailable | Active DOM pointer |
| Touch/pen | GLFW unavailable | GLFW unavailable | PointerEvent properties |
| IME | Existing IMM | Committed Unicode | Existing textarea composition |
| Fonts | System GDI enumeration | Discovery adapter unavailable; bitmap fallback | Local Font Access permission/CSS checks |
| Printing/accessibility | Native dialog/MSAA | Adapter unavailable | Print dialog/semantic DOM |
| Acquisition | Image picker | Picker adapter unavailable | Image input/mobile capture |
| Associations/activation | Per-user registration/launch resources | Launch resources; registration unavailable | Installed web app launchQueue only |

Capability support can still encounter permissions, unavailable devices, focus or
temporary failures. Cancelled pickers return empty/null; cancellation tokens raise
cancellation. `Tasks.UseCancellation` applies a token to subsequent calls; embedders
pass CancellationToken to async contracts. Browser codecs terminate and compression
yields between chunks. Native modal dialogs check cancellation before/after the dialog;
they finish when closed. Clear the token with
`Tasks.UseCancellation(Serialization.ParseJson("null"))`.

## Example and verification

[The portable example](../examples/application_libraries/main.aqua) exercises documents,
ZIP, temporary PNG save/open, stream seeking, editing and UTF-16 composition. For a
user save, call `Application.SaveFile({"suggestedName":"drawing.aqd"})`, check for null,
then `Files.SaveBytes(resource,Serialization.Binary(document,1))`. For IME integration:

```text
p.on("textInput", 函式(){ editor.Commit(p.inputText); });
p.on("compositionUpdated", 函式(){ editor.Composition(p.compositionText,p.compositionCursor,"Utf16"); });
p.on("compositionEnded", 函式(){ editor.CancelComposition(); });
```

Build the native bridge, restore .NET packages, and run `npm ci` and
`npm run prepare:browser` in AquariusWebCompiler before building. Magick.NET Q8 14.17.2
uses Apache-2.0 and requires no build-time license key. Its native ImageMagick runtime
and bundled dependencies' notices ship in `licenses/MAGICK-NET-NOTICES.txt` alongside
`licenses/MAGICK-NET-LICENSE.txt`. ImageMagick WASM 0.0.44/fflate 0.8.3 licenses/notices
ship with every website. Desktop codec operations serialize access to ImageMagick's
process-wide resource limits, disallow disk caches and external delegates, and check
cancellation before and after native operations. Run `dotnet test AquariusTests` and
`npm test --prefix AquariusWebCompiler`. Tests cover corruption/expansion, codecs,
alpha/metadata/pages, ownership, settings migration/cancellation and desktop-to-browser
document/ZIP/compression/TIFF parity. Set `AQUARIUS_APPLICATION_NATIVE_TESTS=1` on Windows
for hidden-window lifecycle/capture/close/Unicode and actual OS accessibility coverage.
After exporting examples to `.web-build/application`, run
`node AquariusWebCompiler/tests/application-browser.mjs` for real Chromium resource,
worker, clipboard, drop, key, picker, persistence, cancellation, accessibility and
acquisition checks. Printing is intercepted at its dialog boundary to check preparation
without sending a physical page. Existing package/example/smoke/resize checks still apply.
