# Portable bottle format

`AquariusPackaging` depends only on `AquariusLangVM` and framework libraries.
Both desktop and web hosts use `BottlePackage.Load`. The container is ZIP; module
contents retain the existing version 1 RIUS binary bytecode format.

New builds write this version 2 `bottle.json` manifest:

```json
{
  "format": "aquarius-bottle",
  "version": 2,
  "entryPoint": "main.rius",
  "bytecodeVersion": 1,
  "modules": ["main.rius", "lib/tools.rius"],
  "assets": ["assets/image.png", "assets/shader.wgsl"]
}
```

The listed modules and assets must exactly describe the archive. Every module
is validated by `BytecodeSerializer` before a host receives it. Assets may be
arbitrary resource bytes, including empty files and supporting Python scripts;
Aquarius source and bytecode are not permitted as assets. Source compilation
and web export never execute application code. Package build output is
deterministic for identical ordered module inputs, assets and selected entry.
The writer replaces the destination only after successful compilation and
serialization.

Version 1 manifests (`format`, `version`, `entryPoint`) remain readable and
executable; all non-manifest entries are `.rius` modules. They can also be
exported to the browser, with no bundled assets. Old version 1-only readers do
not accept newly written version 2 bottles. Package, RIUS and browser transport
versions are separate contracts; changing package metadata does not change the
RIUS instruction format.

Limits: 10,000 modules/resources combined, 256 MiB uncompressed including the
manifest, 64 MiB per bytecode module, and 1 MiB for a version 2 manifest (16 KiB
for version 1). Paths use `/`, are relative, and disallow traversal, empty
components, Windows reserved names, alternate data streams and nonportable
characters. Duplicate names are rejected case-insensitively. Input symlinks
are not followed. Missing entries, undeclared resources, unknown versions and
malformed/truncated instructions are rejected.
