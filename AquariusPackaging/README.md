# Portable WebAssembly applications

`AquariusPackaging` depends only on core and framework libraries. `WasmApplication`
compiles source modules and assets into one standard WebAssembly 1.0 `.wasm` file.
It has the usual Wasm types, imports, functions, exports and code sections, plus
one `aquarius.application` custom section, plus a raw `aquarius.assets` section when
assets are embedded. There is no ZIP container or Aquarius bytecode format.

Metadata ABI version 3 is binary (`WasmProgram.Create` writes it): the value ABI, program
address, heap start, function layout and count, `modules` (relative `.aqua` identities to
compiled function indices), the entry, supported platforms, whether assets are external, and
each asset's path and length. Asset bytes follow in order in `aquarius.assets`. Constant pools,
function frames and parameter names live in the program data segment, not in metadata.
Metadata never contains executable Aquarius instructions or source text. ABI 2 artifacts
(JSON metadata with constant pools and base64 assets) still load. The host import module is
`aquarius_v2`, and compiled exports are `aqua_fN(i32) -> i32`.

All inputs must remain within `--root`, without symbolic-link traversal. Modules
retain Unicode paths and case-insensitive ordinal resolution; internal `..`
traversal is normalized, while escapes, device names, streams, duplicate paths,
unsupported ABI versions and malformed metadata are rejected. Every module must
be explicitly listed, including potential dynamic imports. Assets may include
empty files and external scripts but exclude Aquarius sources and compiled inputs.
The application size limit is 256 MiB and the path-entry limit is 10,000.

Compilation is deterministic for identical ordered source modules, assets and entry.
It never executes source and replaces output only after all inputs compile. Web and
EXE exporters use the same artifact and preserve previous output on failure.
Runtime imports execute the compiled function for the resolved module; repeated
imports receive independent globals. Closures retain their defining module context.
Desktop resources are temporary, while generated output files use the application's
relocated module directory.

`.bottle` and `.rius` are retired formats. Rebuild from `.aqua` sources.
Executable overlays preserve the existing deployment design with format version 2,
checksummed Wasm payload and selected entry; see [platform targets](../AquariusBuild/README.md).
