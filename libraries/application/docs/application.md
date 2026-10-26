# Application libraries: API guide

This guide describes the modules of the application library and, for `Window` and `Fonts`, the graphics library.
Validation, document formats, codecs, compression and editing are C code shared by every target; the per-OS layers
and their capabilities are in the [library README](../README.md#platforms).

## API reference

Import modules with `匯入("Files")` or `import("Files")`. Members have English and Traditional Chinese aliases;
`src/modules.def` lists them with their argument counts, and `aquarius --catalog` publishes them to editor tooling.
Records have lower camel case fields; nested data uses string-keyed hashes. Binary
values are arrays of integers in 0..255. Failures never report successful no-ops.

| Module/object | Members |
|---|---|
| `Files` | `Capabilities()`, `Resolve(location)`, `Stat(resource)`, `ReadBytes(resource)`, `ReadText(resource,encoding)`, `SaveBytes(resource,bytes[,requireAtomic])`, `SaveText(resource,text,encoding[,requireAtomic])` |
| `Files` | `Open(resource,mode)`, `Enumerate(directory)`, `CreateDirectory(directory)`, `Delete(resource[,recursive])`, `Copy(source,destination[,overwrite])`, `Move(source,destination[,overwrite])`, `Temporary(isDirectory)` |
| `Files` | `ReadAssetBytes(path)`, `ReadAssetText(path,encoding)` — packaged read-only assets, source-relative on desktop source execution |
| stream | `Read(count)`, `Write(bytes)`, `Seek(offset,"Begin"/"Current"/"End")`, `Flush()`, `Close()`; `canRead`, `canWrite`, `canSeek` |
| `Paths` | `Normalize(path)`, `Join(parts)`, `Name(path)`, `Extension(path)`, `Parent(path)`, `Relative(fromDirectory,target)` |
| `Images` | `Formats()`, `Inspect(bytes)`, `Create(width,height,rgbaBytes)`, `Decode(bytes,frame)`, `Load(resource,frame)`, `Encode(image,format,options)`, `Save(resource,image,format,options)` |
| image | `width`, `height`, `pixelFormat`, `alpha`, `metadata`, `Pixels()` |
| `Clipboard` | `Capabilities()`, `Formats()`, `ReadText()`, `ReadImage()`, `WriteText(text)`, `WriteImage(image[,textRepresentation])` |
| `Window` | `Attach(glfwWindow)`, `Current()` for Processing, `FromProcessingImage(image)`, `ToProcessingImage(image [, reusableImage])` |
| window integration | `Capabilities()`, `Poll()`, `SetTitle(title)`, `SetCursor(shape)`, `SetCustomCursor(image,x,y)`, `CapturePointer(id)`, `ReleasePointer(id)`, `ResolveClose("Accept"/"Defer"/"Cancel")` |
| `TextEdit` | `Create(text)`, `Convert(text,position,fromUnit,toUnit)` |
| editor | `State()`, `Select(anchor,caret)`, `Insert(text)`, `Backspace()`, `Delete()`, `Move(direction,unit,extendSelection)`, `Convert(position,fromUnit,toUnit)` |
| editor | `Composition(text,cursor[,cursorUnit])`, `CancelComposition()`, `Commit(text)`, `Copy()`, `Cut()`, `Paste()`, `Shortcut(logicalKey,modifierMask)` |
| `Fonts` | `Enumerate()`, `Available(family)`, `Measure(text,family,size)`, `Selection(text,startUtf16,endUtf16,family,size)` |
| `Serialization` | `Json(value)`, `ParseJson(text)`, `Binary(value,schemaVersion)`, `ParseBinary(bytes)`, `TryParseBinary(bytes)` / `嘗試解析二進位(bytes)`, `Compress(bytes,format[,level])`, `Decompress(bytes,format)`, `Archive(resourceHash)`, `ReadArchive(bytes)` |
| `Storage` | `SettingsDirectory()`, `DataDirectory()`, `Read(key)`, `Write(key,bytes)`, `Delete(key)`, `GetPreference(key)`, `SetPreference(key,value)`, `Recent()`, `Remember(resource)` |
| `Application` | `Capabilities()`, `OpenFiles(options)`, `SaveFile(options)`, `PickDirectory()`, `LaunchFiles()`, `AcquireImage()`, `Print(image)`, `Accessibility(nodes)`, `RegisterFileAssociation(options)` |
| `Tasks` | `CreateCancellation()`, `UseCancellation(token)`; token has `Cancel()`, `IsCancelled()`, `Dispose()` |

Resources are opaque, scoped handles with `provider`, `id`, `name`, `kind` (0 file,
1 directory, 2 asset), and optional `persistentIdentity`. An ID need not be an OS path.
`Stat` is authoritative for existence/kind; resolving does not create anything. Reopen
recent descriptors through their provider; descriptors grant no new permissions.
Browser-selected `File`s are read-only and scoped to the run. Packaged assets remain
separate, with unchanged package validation and loading.

`Serialization.TryParseBinary(bytes)` / `嘗試解析二進位(bytes)` returns a record
with `ok`, `schema`, `value`, and `error`. A valid document has `ok=true` and an
empty error. Invalid bytes, checksum failures and invalid input types return
`ok=false`, `schema=0`, `value=null` and diagnostic text, allowing applications
to try a backup without terminating execution. `ParseBinary` retains its throwing
behavior. Both APIs enforce the same binary format validation.

Processing's `inputTimestamp` and `keyTimestamp` use the same monotonic seconds
as Window events. Input callbacks can process only keyboard events at or before
their text commit, preserving rapid key/text ordering. `TextInput.Poll` records
also include `timestamp`. A key event with `state=true` belongs to an active IME
composition; applications should let the IME handle that key.

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

PNG/JPEG/BMP/GIF/TIFF identification uses signatures. Every target decodes and encodes
with the same pinned C codecs (libspng, stb_image, giflib, libtiff and the library's own
BMP code; see the README for versions and unsupported variants), with allocation, frame
and pixel limits. Output pixels are owned, top-down RGBA8, with explicit alpha
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
Queued application events request a redraw of an attached desktop Processing sketch,
including under `noLoop()`, so its draw callback can poll and resolve close requests.

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

Desktop storage uses roaming/local application directories, Android the activity's files
directory; browsers use IndexedDB (`aquarius:<application id>`) subject to origin
quota/eviction. Keys allow 1..128 ASCII letters/digits, dot/underscore/hyphen. Language
preferences use schema 1; missing/newer schemas fail. `Remember` keeps 20 descriptors with
durable identity deduplication. `AQUARIUS_APPLICATION_ID` and `AQUARIUS_STORAGE_ROOT`
choose the application folder and storage root.

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
executable:"C:/path/app.exe",arguments:["--documents"]}`.
Registration explicitly advertises a per-user Open With handler without overriding
default-app choices. `app.wasm --documents file1 file2` supplies `LaunchFiles()`;
embedders can pass launch files to the desktop host/runner. Browser launch resources
require an installed web app with file handlers and `launchQueue`. Static sites cannot
register OS associations or enforce single-instance activation.

What each platform provides is the [platform table](../README.md#platforms) of the library
README; `Capabilities()` reports it at run time. Capability support can still encounter
permissions, unavailable devices, focus or temporary failures. Cancelled pickers return
empty/null; cancellation tokens raise cancellation. `Tasks.UseCancellation` applies a token
to subsequent calls, checked before and after each operation; a picker already shown
finishes when closed. Clear the token with
`Tasks.UseCancellation(Serialization.ParseJson("null"))`.

## Example and verification

[The portable example](../../../examples/application_libraries/main.aqua) exercises documents,
ZIP, temporary PNG save/open, stream seeking, editing and UTF-16 composition. For a
user save, call `Application.SaveFile({"suggestedName":"drawing.aqd"})`, check for null,
then `Files.SaveBytes(resource,Serialization.Binary(document,1))`. For IME integration:

```text
p.on("textInput", 函式(){ editor.Commit(p.inputText); });
p.on("compositionUpdated", 函式(){ editor.Composition(p.compositionText,p.compositionCursor,"Utf16"); });
p.on("compositionEnded", 函式(){ editor.CancelComposition(); });
```

The library's `tests/` run on every target with `scripts/test-libraries.ps1 -Libraries application` (add
`-Devices` for the clipboard and window cases); see the README for what they cover.
