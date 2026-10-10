# Aquarius Language (繁體中文) for Visual Studio Code

Edit `.aqua` files with Traditional Chinese syntax highlighting, bracket pairing,
comment toggling, snippets, and a bundled Language Server Protocol server.
The server provides live syntax diagnostics, keyword/builtin/local completion,
hover documentation, same-document definitions, and an outline of variables,
functions, and parameters. Chinese and other Unicode names are supported.

## Install the packaged extension

1. Install desktop [Visual Studio Code](https://code.visualstudio.com/).
2. Install the [.NET 8 runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
   (or a newer .NET runtime). The SDK is only needed to build from source.
   On Windows, use the **.NET Runtime** installer matching your architecture;
   the Desktop and ASP.NET runtimes are not required.
3. In VS Code's Extensions view, select **… → Install from VSIX…** and choose
   `aquariuslang-tw-0.1.0.vsix`. You can also install from a terminal:

   ```powershell
   code --install-extension "C:\OfficialProjects\AquariusLangTW\editors\vscode\aquariuslang-tw-0.1.0.vsix"
   ```

4. Open a folder you trust and open or create a UTF-8 `.aqua` file. The status bar
   should show **Aquarius (繁體中文)**. If needed, click the language name and select it.
   For other folders, the installed extension activates automatically for `.aqua` files.
5. Try the example below. **Ctrl+Space** offers completion, hovering over `加法`
   shows its parameters, **F12** goes to its declaration, and deleting the `0` in
   the first line shows an error in the Problems panel. Snippet triggers include
   `變數`, `函式`, `如果`, `迴圈`, `印出` and their English equivalents.

```text
變數 計數 = 0;
變數 加法 = 函式(甲, 乙) {
    回傳 甲 + 乙;
};
印出(加法(計數, 2));
```

The bundled server is portable across Windows, macOS, and Linux with a compatible
.NET runtime. In WSL, SSH, or a development container, install the extension and
.NET runtime on the remote host where VS Code runs workspace extensions.

## Build and package from source

Install [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), Node.js 20+
and npm. From the repository root:

```powershell
dotnet build AquariusLanguageServer/AquariusLanguageServer.csproj -c Release
cd editors/vscode
npm ci
npm run check
npm run package
```

`npm run package` publishes the server into `server/`, bundles the client into
`dist/extension.js`, and creates `aquariuslang-tw-0.1.0.vsix` in this directory.
It does not publish anything to the Marketplace. The generated DLLs and VSIX are
ignored by Git; rebuild them after changing server code.

For extension development, open `editors/vscode` as a VS Code folder, run `npm ci`,
and press **F5**. The provided launch task builds the client and server and opens
an Extension Development Host. Open an `.aqua` file there.

For a custom server build, set these **user/machine** settings if needed:

```json
{
  "aquarius.dotnetPath": "C:\\Program Files\\dotnet\\dotnet.exe",
  "aquarius.serverPath": "C:\\OfficialProjects\\AquariusLangTW\\AquariusLanguageServer\\bin\\Release\\net8.0\\AquariusLanguageServer.dll",
  "aquarius.trace.server": "off"
}
```

Leave `aquarius.serverPath` empty to use the bundled server. On macOS/Linux use
absolute paths appropriate to that host. Paths may contain spaces.

## Run a program

Editor language support does not run or debug programs. To execute using this
repository's VM desktop host, install the .NET 8 SDK (which includes the runtime)
and run this from the repository root:

```powershell
dotnet run --project AquariusDesktop -- AquariusDesktop/examples/increment.aqua
```

Or run an already-built VM host:

```powershell
dotnet AquariusDesktop/bin/Debug/net8.0/AquariusDesktop.dll AquariusDesktop/examples/increment.aqua
```

## Tests

From the repository root:

```powershell
dotnet test AquariusLang.sln
dotnet build AquariusLanguageServer/AquariusLanguageServer.csproj -c Release
node --test AquariusLanguageServer/tests/lsp.test.js
```

The protocol tests launch the actual server and cover lifecycle, byte framing,
Unicode positions, scopes, edits, diagnostics, malformed/incomplete input, and
every repository example. `AQUARIUS_DOTNET` optionally selects a dotnet executable;
`AQUARIUS_SERVER` optionally selects a server DLL.

After `npm run vscode:prepublish`, the extension can also be tested with an
installed VS Code, from this directory:

```powershell
code --extensionDevelopmentPath="$PWD" --extensionTestsPath="$PWD/test/extension-host.js" --user-data-dir="$env:TEMP/aquarius-vscode-test" --extensions-dir="$env:TEMP/aquarius-vscode-test-extensions" --disable-extensions --disable-workspace-trust
```

This uses a separate test profile and checks completion, hover, definitions,
outline, live diagnostic updates, and restarting the server. It does not install
the extension in your normal profile.

## Troubleshooting and current limits

- If features fail to start, run `dotnet --list-runtimes`, restart VS Code after
  installing .NET, and check **View → Output → Aquarius Language Server**.
  Run **Aquarius: Restart Language Server** from the Command Palette after fixing
  settings. Setting `aquarius.trace.server` to `verbose` records protocol traffic.
- Syntax highlighting and snippets work in an untrusted workspace; the server
  starts after you trust it. Runtime analysis only parses source and never
  executes it or imports modules.
- Diagnostics currently report syntax/lexical errors. Dynamic type errors,
  undefined names, and runtime errors remain the VM's responsibility.
- Completion and definitions resolve declarations in the current document's
  function and loop scopes. Imported module members and cross-file navigation
  are not yet implemented. Static visibility follows source order and cannot
  model every dynamic closure or conditional declaration.
- Extremely deep expressions are limited to 256 levels to keep incomplete or
  pathological source from crashing the server. No formatter, rename provider,
  signature-help provider, or debugger is included in this version.

The implementation follows Microsoft's [LSP 3.17 specification](https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/)
and [VS Code language-server extension guide](https://code.visualstudio.com/api/language-extensions/language-server-extension-guide).
