# macOS (Apple Silicon)

The `macos-arm64` runtime is the same C host core and default libraries as every other target, with the macOS platform
layer (`runtime/platform/macos`, `posix`, `desktop`): GLFW windows on Cocoa, WebGPU (wgpu-native) on Metal through a
`CAMetalLayer`, text through Core Text. It is built with the pinned LLVM Clang against the SDK of the installed Xcode or
Command Line Tools and linked by Apple's `ld`; .NET is needed only to run the compiler, never by applications.

## Building

Install Xcode (or the Command Line Tools with `xcode-select --install`; iOS needs the whole Xcode) and select it with
`sudo xcode-select -s /Applications/Xcode.app`. The scripts are the same PowerShell scripts as on Windows, run by
PowerShell 7 (`pwsh`); .NET 8, PowerShell 7 and Node.js 24 may live in your home folder. Homebrew is not needed.

```sh
pwsh scripts/build-runtime.ps1 -Targets macos-arm64,web      # the pinned toolchain, then <native build>/install/<target>
dotnet test compiler/AquariusTests -c Release
pwsh scripts/test-libraries.ps1 -Targets macos-arm64
```

Keep the native build folder out of folders iCloud Drive syncs (Desktop and Documents may be): iCloud renames folders it
sees change at the same time ("llvm-23.1 2.3-macos-arm64"). `AQUARIUS_NATIVE_BUILD=$HOME/.aquarius/native-build` moves it;
the scripts and the compiler both read it.

## Applications

```sh
aqua run app.wasm
aqua build app.wasm --target macos -o App.app                  # a signed app bundle
aqua build app.wasm --target macos -o App.app --optimize true  # ahead-of-time compiled (wasm2c), App.aot.dylib inside
open App.app        # or run App.app/Contents/MacOS/App in a terminal to see its output
```

Every Apple Silicon binary must be code signed, and bytes appended to a Mach-O break its signature, so macOS exports are
app bundles rather than the launcher-with-overlay of Windows and Linux: the runtime pack's launcher, unmodified, is
`Contents/MacOS/<Name>`, the overlay alone (the same bytes the other desktops append) is
`Contents/Resources/application.aquarius`, the pack's libraries and external libraries sit beside the launcher, and the
bundle is signed ad hoc inside out, so `codesign --verify --deep --strict` accepts it. Distributing outside your own
Macs needs a Developer ID signature and notarization (with the hardened runtime, Wasmtime's JIT needs the
`com.apple.security.cs.allow-jit` entitlement); the compiler does not do that yet.

## What differs from the other desktops

- Shortcuts use Command (`interaction` reports Apple conventions); `tests/<case>.macos.expected` records the cases whose
  output differs.
- File pickers are the system panels through `osascript`; settings and data live in `~/Library/Application Support`,
  caches in `~/Library/Caches/aquarius` (or `$XDG_CACHE_HOME/aquarius`).
- Not yet available: the OpenGL library (macOS deprecates OpenGL; WGPU and Processing render through Metal) and the
  webcam (`camera` needs an AVFoundation backend).
