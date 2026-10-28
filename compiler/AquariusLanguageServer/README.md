# Aquarius Language Server

A standalone .NET 8 LSP server built on the compiler's lexer, parser and AST. Chinese, English and mixed syntax
receive the same diagnostics, source ranges, keyword completion and hover help. Keywords include `變數` / `var`,
`函式` / `function`, `回傳` / `return`, and `否則如果` / `else if` / `elif`. Build with
`dotnet build compiler/AquariusLanguageServer -c Release` from the repository root, then configure an LSP editor to
launch:

```text
dotnet /absolute/path/to/AquariusLanguageServer/bin/Release/net8.0/AquariusLanguageServer.dll
```

Use standard input/output transport, language ID `aquarius`, and extension `.aqua`. Stdout is reserved for UTF-8
JSON-RPC messages with `Content-Length` headers; stderr carries logs. Positions use zero-based lines and UTF-16
characters. The server supports `initialize`, `initialized`, `shutdown`, `exit`, `textDocument/didOpen`, incremental
or full `textDocument/didChange`, `textDocument/didClose`, push diagnostics, completion, hover, definition,
document symbols and signature help (`textDocument/signatureHelp`, triggered by `(` and `,`: the parameters of a
declared function, a struct construction, a portable or native module member or a core global, in the language the
call is written in). Document contents come from the client, including unsaved buffers. Unsupported requests return
JSON-RPC MethodNotFound; unsupported notifications are ignored. No source is executed during analysis.

## Modules

Nothing about libraries is hard-coded in the server (`ModuleCatalog`):

- **Native modules** come from the runtime's own catalog: `aquarius --catalog` of the host runtime pack of the
  installation the server finds (`AQUARIUS_HOME`, the distribution or checkout around it, or the `aqua` on `PATH`),
  with `--library` for the project's external libraries. Names, aliases and arities are what the runtime registers.
- **Aquarius-source library modules** (`[[modules]]` of the default libraries' and the project's manifests) resolve to
  their source files: completion, hover and definition of their exports, by every import name.
- **The language's portable modules** (Array/陣列, Hash/雜湊, String/字串, Thread/執行緒) and the core globals'
  descriptions are language facts (`LanguageFacts`): every function's parameters in both languages (optional ones end
  in `?`) and what it does. Tests keep the names and arities equal to the guest runtime's tables.

Native functions carry no static result types, so members of the values native calls return are not completed.

## Projects

[Bottle projects](../AquariusBottle/README.md) share their manifest parser and dependency inspection with the
compiler. Open `aqua.toml` with this server for TOML syntax and schema diagnostics (manifest_version 1 and 2, including
`[library]`, `[native]`, `[[modules]]` and `[android]` tables), dependency and asset checks, and manifest key
completion. `/bottles/NAME/path.aqua` imports support member completion, hover and definition in local libraries and
locked global cache snapshots. Analysis is read only and never downloads dependencies; `didSave` and
`workspace/didChangeWatchedFiles` refresh project diagnostics.

Functions declared as `變數 加法, add = 函式(...) { ... };` expose both aliases in completion, hover, definition and
document symbols. Script imports resolve relative to the importing file; string literals, local string constants,
concatenation and `目前工作目錄` / `currWorkingDir` paths resolve statically.

Tests: `dotnet test compiler/AquariusTests --filter FullyQualifiedName~LanguageServer` (they drive the server over
stdio). See [VS Code setup](../../editors/vscode/README.md).
