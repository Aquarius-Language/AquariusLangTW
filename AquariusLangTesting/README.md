# Aquarius tests

This project owns all .NET tests in `AquariusLang.sln`:

- `ast/`, `evaluator/`, `lexer/`, `object/`, `parser/`, `utils/` and the root syntax tests cover the shared language core.
- `desktop/interpret/` covers desktop builtins, script execution and the Starship example.
- `desktop/graphics/` covers OpenGL bindings and Processing, including optional GPU integration tests.

Tests reference the production projects. The desktop assembly grants this test
assembly access to internal graphics types through `InternalsVisibleTo`.
Examples are linked from `AquariusDesktopInterpretedREPL/examples/` and copied
to `examples/` in the test output; resolve them from `AppContext.BaseDirectory`.
Keep example assets and supporting scripts beside their examples.

Run from the repository root with .NET 8 SDK and .NET 6 Runtime:

```powershell
dotnet test AquariusLang.sln
```

`TestExecuteFile` requires `python` on PATH. GPU tests require the
[native graphics library](../native/README.md) and an OpenGL 3.3 desktop driver:

```powershell
./native/build.ps1
$env:AQUARIUS_OPENGL_TESTS = '1'
dotnet test AquariusLangTesting --filter FullyQualifiedName~AquariusREPL.Graphics
```

The language server owns its Node.js protocol tests; the VS Code extension owns
its manifest tests:

```powershell
dotnet build AquariusLanguageServer -c Release
node --test AquariusLanguageServer/tests/lsp.test.js
npm --prefix editors/vscode run check
```
