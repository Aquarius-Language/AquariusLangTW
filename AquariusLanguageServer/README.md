# Aquarius Language Server

A standalone .NET 8 LSP server backed by Aquarius's existing lexer and parser.
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
tests in `tests/lsp.test.js`. No source is executed during analysis.
