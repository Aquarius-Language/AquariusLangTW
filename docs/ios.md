# iOS and iPadOS

Two targets share the `ios` platform: `ios-arm64` (iPhones and iPads) and `ios-simulator-arm64` (the simulator on Apple
Silicon Macs). Both build only on a Mac with Xcode and its iOS platform (`xcodebuild -downloadPlatform iOS` installs the
simulator runtime).

## How an application runs

iOS lets no application generate machine code, and applications carry no loose shared libraries. So an iOS runtime
pack is one static library, `libaquarius.a` (the host core with the ahead-of-time engine `host/engine_aot.c`, the iOS
layer and every default library with their third-party archives), with `frameworks.txt` and the `app/` project, and
`aqua build --target ios` compiles each application ahead of time: the pinned wasm2c translates its `.wasm` to C, and
the pinned Clang links it with the pack into the bundle's executable. This is CLAUDE.md rule 11's portable
ahead-of-time path, the same one `aqua build --optimize` uses on the desktops; the runtime JIT is desktop-only.

`runtime/platform/ios` is the layer: `platform.c` (host services and the runtime thread) and `window.m`, which, like
Android's activity shim, only forwards what UIKit delivers to Objective-C: the scene life cycle, one full-screen view
whose `CAMetalLayer` is the application's window (`AQUA_SURFACE_METAL`), touches as pointer and button events in points,
hardware keyboard keys as GLFW-coded key events and the soft keyboard's text. UIKit's events are queued on the main
thread and dispatched on the runtime thread.

## Building and running

```sh
pwsh scripts/build-runtime.ps1 -Targets ios-simulator-arm64,ios-arm64
aqua build app.wasm --target ios --simulator true -o App.app      # signed ad hoc for the simulator
xcrun simctl install booted App.app && xcrun simctl launch --console-pty booted <bundle id>
pwsh scripts/ios-run.ps1 app.wasm                                 # build, install, run and print its output
pwsh scripts/test-libraries.ps1 -Targets ios-simulator-arm64      # the libraries' tests on a simulator
aqua build app.wasm --target ios -o App.app --bundle-id org.example.app `
    --signing-identity "Apple Development: ..." --provisioning-profile app.mobileprovision   # a device build
```

`--bundle-id` and `--app-name` name the application. Without a signing identity, device builds are signed ad hoc and
link and verify, but a real device installs only applications signed with your identity and provisioning profile.

## Native libraries (not verified yet)

`aqua build <project> --target ios --kind shared,static` builds a project's `匯出` / `export` functions for the device
and the simulator: `<Module>.xcframework` (a framework per slice) and `<Module>-static.xcframework` (a static library
with its headers and module map per slice), with the C header and the Swift binding (`swift/<Module>.swift`) beside
them, for an Xcode app to embed. This is designed and implemented but **has not run on a Mac yet**: the build prints a
one-line notice saying so. [Native library outputs](native-libraries.md) ("macOS and iOS") describes the outputs and
the checklist that verifies them, including the Swift, C, C++ and Rust consumers that
`pwsh scripts/test-exports.ps1` runs on a simulator.

## Not yet available on iOS

- Native code in external (Bottle) libraries: it would have to be linked into the application when it is built.
- `onnx` (ONNX Runtime ships for iOS as an xcframework, not pinned yet), `opengl`, `camera`, `terminal` (no processes),
  the file pickers of `application` (UIDocumentPickerViewController), safe-area insets in `interaction`.
- Running on real hardware has not been tested; the simulator has.
