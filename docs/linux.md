# Linux desktop (Ubuntu 22.04 / WSL2)

The `linux-x64` runtime is the same C host core and default libraries as every other target, with the Linux platform
layer (`runtime/platform/linux`, `posix`, `desktop`). It is built with the pinned LLVM Clang against glibc; .NET is
needed only to run the compiler, never by applications.

## Building

Inside Ubuntu (on Windows: WSL2 with WSLg):

```sh
sudo apt-get update
sudo apt-get install -y build-essential cmake pkg-config dotnet-sdk-8.0 \
  libx11-dev libxrandr-dev libxinerama-dev libxcursor-dev libxi-dev \
  libwayland-dev libxkbcommon-dev wayland-protocols
bash scripts/install-toolchain.sh linux-x64        # pinned LLVM, WASI sysroot, Wasmtime C API
export AQUARIUS_LLVM=$HOME/aquarius-toolchains/llvm-23.1.3-linux-x64   # or the path the installer prints
bash scripts/test-linux.sh                          # builds .native-build/install/linux-x64, then dotnet test
```

From Windows, `scripts/build-runtime.ps1 -Targets linux-x64` builds the same pack in WSL (`-Distribution` picks the
distribution) and `scripts/test-libraries.ps1 -Targets linux-x64` runs the libraries' tests there.

```sh
aqua build examples/increment.aqua -o app.wasm
aqua run app.wasm
aqua build app.wasm --target linux -o app    # aquarius with the application appended; libraries/ beside it
./app
```

Exports are marked executable; transfers that drop permissions need `chmod +x app`. `bash scripts/publish-cli.sh`
publishes the compiler distribution to `dist/aqua-linux-x64`.

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
