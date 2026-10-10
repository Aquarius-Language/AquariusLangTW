# Aquarius Language Server

A standalone .NET 8 LSP server using the lexer, parser and AST owned by
`AquariusCore`. It references the portable VM library for the language frontend.
Build with `dotnet build AquariusLanguageServer/AquariusLanguageServer.csproj -c Release`
from the repository root, then configure an LSP editor to launch:

```text
dotnet /absolute/path/to/AquariusLanguageServer/bin/Release/net8.0/AquariusLanguageServer.dll
```

Use standard input/output transport, language ID `aquarius`, and extension `.aqua`.
Stdout is reserved for UTF-8 JSON-RPC messages with `Content-Length` headers;
stderr carries logs. Positions use zero-based lines and UTF-16 characters.
The server supports `initialize`, `initialized`, `shutdown`, `exit`,
`textDocument/didOpen`, incremental or full `textDocument/didChange`,
`textDocument/didClose`, push diagnostics, completion, hover, definition,
and document symbols. Document contents come from the client, including unsaved
buffers. Closing a document clears its diagnostics. Unsupported requests return
JSON-RPC MethodNotFound; unsupported notifications are ignored.

See [VS Code setup and limitations](../editors/vscode/README.md) and the integration
tests in [tests/lsp.test.js](tests/lsp.test.js). Run them from the repository root
with `node --test AquariusLanguageServer/tests/lsp.test.js` after the Release build.
No source is executed during analysis.

Functions declared as `變數 加法, add = 函式(...) { ... };` expose both aliases in
completion, hover, definition and document symbols. Builtins and all desktop
native libraries offer Chinese and English completion/hover from the portable
`LibraryCatalog`; the server never initializes graphics or physics. Member
completion is triggered by `.` and follows known return types such as Jolt
worlds, WGPU devices/buffers/shaders/targets and Processing vectors/images/canvases.

Script imports resolve relative to the importing file. String literals, local
string constants, concatenation and `目前工作目錄` / `currWorkingDir` paths can be
resolved statically. Open unsaved library buffers take precedence over disk files,
including after edits. Both script aliases jump to their own declaration tokens.
Dynamic runtime import paths and arbitrary computed return types remain unknown;
unknown receivers do not receive unrelated member suggestions.
