# Portable WebAssembly applications

`AquariusPackaging` depends only on core and framework libraries. `WasmApplication`
compiles source modules and assets into one standard WebAssembly 1.0 `.wasm` file.
It has the usual Wasm types, imports, functions, exports and code sections, plus
one `aquarius.application` custom section. There is no ZIP container or Aquarius
bytecode format.

Metadata ABI version 1 declares `entry`, `modules` (relative `.aqua` identities to
compiled function indices), `functions` (constant pools and return behavior), and
`assets` (relative paths to base64 content). Nested functions refer to exported
Wasm function indices; metadata never contains executable Aquarius instructions.
The host ABI is `aquarius_v1`, and compiled exports are `aqua_fN(i32) -> i32`.

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
