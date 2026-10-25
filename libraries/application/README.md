# application

The application library of AquariusLang: files and streams, lexical paths, image codecs, JSON and AQD1 documents,
compression and ZIP archives, application storage and preferences, the clipboard, pickers, printing, accessibility,
cancellation and grapheme-aware text editing. Everything is C17 compiled to the same algorithm on every target (the web
included); only thin per-OS layers in `src/platform/` reach the operating system or, on the web, the glue domains
`web-glue/storage`, `web-glue/files`, `web-glue/clipboard`, `web-glue/print` and `web-glue/accessibility`.

## Modules

Names, aliases and argument counts are the old `ApplicationCatalog`'s (`src/modules.def` is the table the library
registers). Failures stop the script with `Name: Kind: message`, the kinds being `Unsupported`, `PermissionDenied`,
`Unavailable`, `Cancelled`, `InvalidData`, `LimitExceeded`, `NotFound` and `Conflict`.

| Module | Members |
| --- | --- |
| `Files` | `Capabilities`, `Resolve`, `Stat`, `ReadBytes`, `ReadText`, `SaveBytes`, `SaveText`, `Enumerate`, `CreateDirectory`, `Delete`, `Copy`, `Move`, `Temporary`, `Open`, `ReadAssetBytes`, `ReadAssetText` |
| resource | `provider`, `id`, `name`, `kind` (0 file, 1 directory, 2 asset), `persistentIdentity`, `Dispose()` (temporary resources) |
| stream | `canRead`, `canWrite`, `canSeek`, `Read(count)`, `Write(bytes)`, `Seek(offset, "Begin"/"Current"/"End")`, `Flush()`, `Close()` |
| `Paths` | `Normalize`, `Join`, `Name`, `Extension`, `Parent`, `Relative` |
| `Images` | `Formats`, `Inspect`, `Create`, `Decode`, `Load`, `Encode`, `Save` (images are the core image objects of `aqua/image.h`) |
| `Serialization` / `序列化` | `Json`, `ParseJson`, `Binary`, `ParseBinary`, `TryParseBinary`, `Compress`, `Decompress`, `Archive`, `ReadArchive` |
| `Storage` | `SettingsDirectory`, `DataDirectory`, `Read`, `Write`, `Delete`, `GetPreference`, `SetPreference`, `Recent`, `Remember` |
| `Clipboard` | `Capabilities`, `Formats`, `ReadText`, `ReadImage`, `WriteText`, `WriteImage` |
| `Application` | `Capabilities`, `LaunchFiles`, `OpenFiles`, `SaveFile`, `PickDirectory`, `Print`, `AcquireImage`, `Accessibility`, `RegisterFileAssociation` |
| `Tasks` | `CreateCancellation`, `UseCancellation`; token `Cancel`, `IsCancelled`, `Dispose` |
| `TextEdit` | `Create`, `Convert`; editor `State`, `Select`, `Insert`, `Backspace`, `Delete`, `Move`, `Convert`, `Composition`, `CancelComposition`, `Commit`, `Copy`, `Cut`, `Paste`, `Shortcut`, `Undo`, `Redo`, `CanUndo`, `CanRedo`, `DeleteWord`, field `positionUnit` |

Records (`Stat`, `Inspect`, `ParseBinary`, `TryParseBinary`, `State`, the capability records) are `record` objects whose
fields read like the old ones (`info.width`, `state.selection["caret"]`); nested values are string-keyed hashes. The
`Window` integration module and `Fonts` belong to the graphics library.

Limits: 64 MiB of encoded, expanded or transferred bytes, 8192 pixels per image side, 16 Mi pixels over all frames,
256 frames, 4096 archive or directory entries, JSON nesting of 64 and editor text of 1 Mi UTF-16 units.

## Platforms

| Capability | Windows | Linux | Android | Web |
| --- | --- | --- | --- | --- |
| Files, streams, temporary resources | Win32 (staged `ReplaceFileW`, transient-lock retries) | POSIX | POSIX, plus `content://` documents through the ContentResolver | `persistent:/` (IndexedDB), `download:/`, `temporary:/`, picked files |
| Storage directories | `%APPDATA%` / `%LOCALAPPDATA%` | `$XDG_CONFIG_HOME` / `$XDG_DATA_HOME` | the activity's files directory | IndexedDB `settings/<key>` records |
| Codecs, documents, compression, ZIP, editing | same C code | same | same | same (WebAssembly) |
| Clipboard | text, PNG and DIBV5 images | text (window system, GLFW) | text; images read from content URIs | `navigator.clipboard` text and PNG |
| Pickers and image acquisition | `IFileOpenDialog` / `IFileSaveDialog` | zenity | Storage Access Framework | File System Access API, `<input type=file>`, downloads |
| Printing | print dialog and GDI page | unsupported | unsupported | `window.print()` of an image frame |
| Accessibility | read-only MSAA tree on the application window (`WM_GETOBJECT`) | accepted, not published | the activity view's content description | semantic DOM mirror |
| File associations | per-user Open With registration | unsupported | unsupported | unsupported |
| Launch documents | launch arguments (`app.wasm --documents a b`) | launch arguments | launch arguments, the activity intent's document | `launchQueue` of an installed web app |

On the desktop, `AQUARIUS_STORAGE_ROOT` points Storage at another directory (settings there, data in `data/`) and
`AQUARIUS_APPLICATION_ID` names the application folder (default `AquariusLang`); on the web the latter names the
IndexedDB database (`aquarius:<id>`, default `aquarius:application`). Both are read through `aqua_environment`.

## Dependencies

All C, downloaded by `CMakeLists.txt` from their official release URLs and checked against pinned SHA-256 hashes
(cached in `AQUA_APPLICATION_DOWNLOADS`), then compiled into the library itself. Every external symbol of that code is
renamed `aqua_application_*` by `third_party/private_symbols.h`, which the build force-includes, so another default
library vendoring the same code never clashes with it in the runtime executable. The header lists what
`llvm-nm --defined-only --extern-only` reports for the pinned objects; after changing a pin, check the built library
with `llvm-nm --defined-only --extern-only <lib> | grep -v "^aqua_application_\|^app_"` (only
`aqua_library_application` may remain) and add new names.

| Library | Version | License | Used for |
| --- | --- | --- | --- |
| zlib | 1.3.2 | zlib | Gzip/Zlib/Deflate, ZIP entries, PNG and TIFF Deflate |
| libspng | 0.7.4 | BSD-2-Clause | PNG decoding (every color type and depth, interlacing, tRNS, pHYs, eXIf, PLTE) |
| stb_image / stb_image_write | 2.30 / 1.16 (commit `2c980bb`) | MIT or public domain | JPEG decoding and encoding (compiled as static functions) |
| giflib | 5.2.2 | MIT | GIF decoding and encoding, median-cut quantization |
| libtiff | 4.7.2 | libtiff (BSD-style) | TIFF decoding (no JPEG, JBIG, LZMA, ZSTD, WebP or LERC codecs) |
| Brotli | 1.2.1 | MIT | Brotli compression |
| utf8proc | 2.12.0 | MIT (Unicode data: Unicode license) | extended grapheme clusters and general categories |

Why these: libpng and libjpeg(-turbo) report errors by `longjmp`, which WebAssembly side modules cannot do without the
exception-handling proposal and loader support, so PNG uses libspng (fuzzed, error codes, small) and JPEG uses
stb_image/stb_image_write (static, small, baseline and progressive Huffman JPEG). giflib and libtiff are the reference
implementations and report errors as return values. BMP (all header versions, bit fields, RLE4/RLE8), the old PNG and
TIFF writers, AQD1, JSON and ZIP are written here. ImageMagick and browser image decoding are not used.

## Building and testing

The folder builds inside the runtime (`-DAQUA_LIBRARIES=application`) or alone as an external library. Tests are
`tests/<case>.aqua` with the exact output in `tests/<case>.expected`; a first line marked `(web only)`,
`(desktop only)` or `(windows only)` limits a case. They cover documents, JSON, AQD1, compression and ZIP, paths,
editing, the five codecs with metadata and frames, files on temporary resources and streams, storage and recent
resources, cancellation, capability records, the desktop clipboard and web persistence. The frozen compiler reads the
nearest `aqua.toml`, so compile the cases from a copy outside this folder.

## Differences from the previous implementation

- Codecs are C libraries instead of Magick.NET / ImageMagick WASM. `metadata.sourcePixelFormat` keeps the desktop form
  `"<ColorType>, <depth> bits/channel"` (`TrueColorAlpha`, `Palette`, `Grayscale`, `ColorSeparation`, ...) derived from
  the file's header. Unsupported: TIFF pages with JPEG compression, arithmetic-coded, 12-bit and lossless JPEG. JPEG
  output is stb's baseline encoder (4:2:0 below quality 90), so sizes differ. GIF output uses an exact palette up to 256
  colors and giflib's median cut beyond. BMP output is 24-bit and carries no resolution when the image has none.
- The web runs codecs on the page's thread (there is no codec worker or 35 s codec time limit); web streams left open
  when the run ends are discarded, not committed (close or flush them).
- `Serialization.Json` writes non-ASCII text as UTF-8 instead of `\uXXXX` escapes and numbers in ECMAScript form
  (`1e+21`, `1e-7`); parsing is strict RFC 8259 as before.
- ZIP64 archives are rejected on every target (64 MiB documents do not need them); truncated raw Deflate fails instead
  of returning partial data.
- `Files.Enumerate` returns entries sorted by name. On the web, temporary directories now copy and move (in memory),
  `Storage.SettingsDirectory()`/`DataDirectory()` are directories (`kind` 1) without a persistent identity as on the
  desktop, and the IndexedDB database is `aquarius:<AQUARIUS_APPLICATION_ID or application>` instead of the entry script.
- `Application.LaunchFiles()` resolves every launch argument the runtime received (it strips `--documents`).
- Records are `record` objects (`typeOf` gives `record`) instead of modules; resources, streams, editors and tokens are
  `resource`, `stream`, `editor` and `cancellation` objects. Argument-type errors come from the runtime's signature
  checks.
- Cancellation is checked before and after each operation; a picker already shown is not interrupted.
- Android prints nothing and writes no clipboard images (both need Java components the activity does not have yet);
  Linux keeps the accessibility node list without publishing it.
