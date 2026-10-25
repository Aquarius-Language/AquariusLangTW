# AquariusCompiler

The AquariusLang compiler: source to a standard WebAssembly application that the native runtime (`runtime/`) runs on
every target. Chinese, English and mixed syntax share one lexer, parser and compiler; see
[the language reference](LANGUAGE.md).

```text
lexer/, parser/, ast/, token/   the front end
object/                         the IR's constant values
LoweringCompiler, LoweredProgram   lowering to the compiler-only IR (never serialized or interpreted)
wasm/                           WebAssembly emission: WasmCompiler links the guest runtime (runtime/guest/runtime.wasm,
                                embedded) with the compiled functions; RuntimeLayout reads runtime/guest/layout.def
WasmApplication                 application packaging: modules, assets, platforms and the ABI 4 metadata
libraries/                      library manifests (Library), the libraries a compilation sees (LibrarySet) and linking
                                (ApplicationLinker)
AquariusInstallation            where libraries/, runtime packs, web-glue/ and the SDK are (a distribution or checkout)
```

## Application metadata (ABI 4)

A `.wasm` application has one `aquarius.application` custom section and, when assets are embedded, one raw
`aquarius.assets` section. The metadata is binary: magic `00 'AQM'`, version 4, the value ABI, program address, heap
start, function layout and count, the script modules (relative `.aqua` paths with their function index), the entry,
supported platforms, asset storage and the assets' names and lengths, then

```text
uleb importCount, { string name, uleb moduleIndex } *   every import name (each alias) of a bundled Aquarius-source
                                                         library module -> its script module index
uleb libraryCount, { string name } *                     the native libraries the application needs
```

The runtime (`runtime/host/application.c`) reads ABI 3 and 4; the compiler writes 4 and still reads 3 (to export
older applications). ABI 2 (JSON metadata) is rejected with a rebuild message.

## Libraries

`LibrarySet.Default` is every `libraries/<name>/aqua.toml` of the installation; projects add their external libraries
(Bottle packages, `manifest_version = 2`). When a program imports a library module by a literal name, the compiler
bundles the library's Aquarius-source modules at `.aquarius/<library>/<source>` and lists its native part in the
metadata. See [libraries and external libraries](../../docs/external-libraries.md).

```csharp
using AquariusLang.Wasm;
var program = new WasmCompiler().Compile("變數 答案 = 6 * 7; 答案;");
File.WriteAllBytes("app.wasm", program.Bytes.ToArray());   // aqua run app.wasm prints 42
```

Compilation never runs code, is deterministic for the same inputs, and replaces an output file only after everything
compiled. Package paths are relative, `/`-separated, free of dot segments, device names and characters Windows rejects;
the size limit is 256 MiB and 10,000 entries.
