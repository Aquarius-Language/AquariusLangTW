# Linux desktop (Ubuntu 22.04 / WSL2)

The `linux-x64` runtime is the same C host core and default libraries as every other target, with the Linux platform
layer (`runtime/platform/linux`, `posix`, `desktop`). It is built with the pinned LLVM Clang against glibc; .NET is
needed only to run the compiler, never by applications.

## Building

Inside Ubuntu (on Windows: WSL2 with WSLg):

The build and test scripts are the same PowerShell scripts as on Windows, run by PowerShell 7 (`pwsh`), which installs
as a .NET tool. The compiler (C#) and the toolchain (the pinned LLVM, CMake and Ninja that `build-runtime.ps1`
downloads into `.native-build`) need nothing else from the distribution than the window system's headers:

```sh
sudo apt-get update
sudo apt-get install -y dotnet-sdk-8.0 curl xz-utils \
  libx11-dev libxrandr-dev libxinerama-dev libxcursor-dev libxi-dev \
  libwayland-dev libxkbcommon-dev wayland-protocols libgl1-mesa-dev
dotnet tool install --global PowerShell && export PATH="$PATH:$HOME/.dotnet/tools"   # Node.js 24 too, for web runs
pwsh scripts/build-runtime.ps1 -Targets linux-x64,web   # the pinned toolchain, then .native-build/install/<target>
dotnet test compiler/AquariusTests -c Release
pwsh scripts/test-libraries.ps1 -Targets linux-x64,web  # web runs need `npm ci --prefix scripts` and Playwright's Chromium
pwsh ./build-release.ps1 -Targets linux-x64,web          # the release for this host (dist/aqua, dist/releases)
```

From Windows, `scripts/build-runtime.ps1 -Targets linux-x64` builds the same pack in WSL (`-Distribution` picks the
distribution; its Linux toolchain is installed in the distribution's `~/.aquarius/native-build`) and
`scripts/test-libraries.ps1 -Targets linux-x64` runs the libraries' tests there. Without PowerShell,
`scripts/install-toolchain.sh` installs the pinned toolchain alone (the compiler uses it for `aqua build --optimize`).

```sh
aqua build examples/increment.aqua -o app.wasm
aqua run app.wasm
aqua build app.wasm --target linux -o app    # aquarius with the application appended; libraries/ beside it
./app
```

Exports are marked executable; transfers that drop permissions need `chmod +x app`. `pwsh scripts/publish-cli.ps1` publishes
the compiler distribution alone; `pwsh ./build-release.ps1` also tests and archives it.

## Run-time requirements

| Feature | Needs |
| --- | --- |
| Windows, input | X11 or Wayland (GLFW 3.4; `AQUARIUS_GLFW_PLATFORM=x11` or `wayland` forces one; WSLg defaults to X11) |
| Graphics, compute, ONNX WebGPU | a Vulkan driver and `libvulkan.so.1` (Mesa's software driver works) |
| OpenGL examples | OpenGL 3.3 |
| Text | Pango and Cairo when installed (otherwise a built-in bitmap font); installed fonts for CJK fallback |
| Pickers | `zenity` |
| Audio | ALSA or PulseAudio/PipeWire (miniaudio loads them at run time) |
| Cameras | Video4Linux2 devices with an uncompressed mode |

Linux keeps the application's accessibility node list without publishing it (no AT-SPI); printing, image clipboard
and file associations report Unsupported.
