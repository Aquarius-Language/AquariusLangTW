# 版本 / Versioning

AquariusLang has one version number, a [semantic version](https://semver.org) `MAJOR.MINOR.PATCH`. The language, its
compiler, the `aqua` CLI, the language server and the VS Code extension always share it: an editor using the server of
release 0.1.5 understands exactly the syntax that the 0.1.5 compiler accepts.

星泉只有一個版本號（語意化版本 `主.次.修訂`）。語言、編譯器、`aqua` 命令列、語言伺服器與 VS Code 擴充功能永遠使用同一個版本，
因此編輯器顯示的語法與同版本編譯器接受的語法完全一致。

## Where it lives / 版本來源

| Place | What reads it |
| --- | --- |
| `<Version>` in [`compiler/Directory.Build.props`](../compiler/Directory.Build.props) | The single source: every compiler assembly, `AquariusVersion.Current`, `aqua --version`, the language server's `serverInfo.version` |
| `version` in `editors/vscode/package.json` and `package-lock.json`, the VSIX name in its `package` script | Must equal the props version; tests fail otherwise |

`aqua --version` prints `AquariusLang 0.1.5`. The VS Code extension compares the server's `serverInfo.version` with its
own and warns when `aquarius.serverPath` points at a server from another release.

## Requiring a version / 指定需要的版本

A project or library may state the language versions it works with in `aqua.toml`, with the same constraints as
[dependencies](../compiler/AquariusBottle/README.md):

```toml
[project]            # or [library]
name = "app"
aquarius = ">= 0.1.4, < 0.2"
```

A compiler (or language server) of any other version rejects the manifest with
`app needs AquariusLang >= 0.1.4, < 0.2; this is 0.1.3.`, so a program written for newer syntax fails with a clear
message instead of confusing parse errors. Leave it out to accept every version.

專案或函式庫可在 `aqua.toml` 的 `[project]` 或 `[library]` 以 `aquarius = ">= 0.1.4"` 指定需要的語言版本；版本不符的編譯器
與語言伺服器會直接回報清楚的錯誤。

## What a new version means / 版本號的意義

- **PATCH** (0.1.4 → 0.1.5): fixes and additions that keep every working program working the same way.
- **MINOR** while the major version is 0 (0.1 → 0.2): anything that can change or break an existing program: a new
  reserved word, a changed meaning, a removed feature. From 1.0 on, such changes need a new **MAJOR** version and
  MINOR is for compatible additions.
- The application ABI (`WasmApplication` metadata) and the library SDK (`AQUA_API_VERSION`) keep their own numbers;
  they change only when compiled applications or native libraries must be rebuilt.

## Releasing / 發布新版本

1. Set `<Version>` in `compiler/Directory.Build.props`.
2. Set the same `version` in `editors/vscode/package.json` and `package-lock.json` and the VSIX name in its `package`
   script.
3. Add the release to [`CHANGELOG.md`](../CHANGELOG.md), listing language changes first.
4. `dotnet test compiler/AquariusTests` and `node --test editors/vscode/test/manifest.test.js` check that every place agrees; then
   `./build-release.ps1` builds the packs, the CLI and `aquariuslang-tw-<version>.vsix`.
