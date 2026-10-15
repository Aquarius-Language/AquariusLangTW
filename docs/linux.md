# Linux desktop (Ubuntu 22.04 / WSL2)

AquariusDesktop and `aqua` support Linux x86_64 using the same compiler, Wasmtime
execution, shared physics/audio Wasm and desktop libraries as Windows. Linux
exports are self-contained ELF executables containing application Wasm, modules,
assets, .NET and native runtime libraries.

Install .NET 8 SDK, Node.js 24 and these Ubuntu packages:

```sh
sudo apt-get update
sudo apt-get install -y dotnet-sdk-8.0 build-essential cmake pkg-config \
  libx11-dev libxrandr-dev libxinerama-dev libxcursor-dev libxi-dev \
  libwayland-dev libxkbcommon-dev wayland-protocols libgl1-mesa-dev \
  mesa-vulkan-drivers libpangocairo-1.0-0 fonts-noto-cjk python-is-python3 zenity ffmpeg
```

GUI applications need X11 or Wayland, OpenGL 3.3 for GL examples, Vulkan for
WebGPU/Processing/compute, and an audio device for live sound. WSL2 uses WSLg;
run these commands inside Ubuntu. Mesa's software Vulkan driver is supported.
Pango/Cairo and installed fonts supply Unicode shaping and CJK fallback.
Zenity supplies file/directory pickers. Linux supports semantic accessibility tree
updates; native AT-SPI publication is not yet provided. Windows-specific
image clipboard, accessibility and printing capabilities remain reported as
unsupported. FFmpeg/V4L2 cameras need a device visible inside Linux.

WSLg defaults to X11 to avoid empty OpenGL framebuffer reads in its Mesa D3D12
Wayland driver. Other Linux desktops retain GLFW's automatic platform selection.
`AQUARIUS_GLFW_PLATFORM=x11` or `wayland` explicitly selects either backend.
The native bridge retains WSLg's D3D12 libraries for the process lifetime so
driver thread-local destructors remain valid after an OpenGL context closes.
On this WSL installation, WebGPU uses Mesa's software Vulkan adapter; native
Linux installations can use their installed hardware Vulkan drivers.

Build and run from the repository root:

```sh
npm ci --prefix AquariusWebCompiler
npm run --prefix AquariusWebCompiler prepare:browser
bash native/build.sh
bash scripts/publish-apphost.sh
dotnet build AquariusLang.sln -m:1
dotnet AquariusCli/bin/linux/Debug/net8.0/aqua.dll build examples/increment.aqua -o app.wasm
dotnet AquariusCli/bin/linux/Debug/net8.0/aqua.dll run app.wasm
dotnet AquariusCli/bin/linux/Debug/net8.0/aqua.dll build app.wasm --target linux -o app
./app
```

Sources can also be passed directly to `build ... --target linux -o app`.
The runtime pack is `build-targets/linux-x64/{host,runtime.json}`. Compiler
distributions include it; `AQUARIUS_BUILD_TARGETS` can point to another pack
directory. Exporting then needs no SDK or network. Exports receive Unix execute
permissions automatically. Transfers through Windows or archives that discard
permissions require `chmod +x app`. Target machines need Linux system/driver/font
libraries; .NET, Aquarius sources and a separate `.wasm` file are unnecessary.

`bash scripts/publish-cli.sh` builds the complete compiler distribution at
`dist/aqua-linux-x64`. Its `aqua` supports `wasm`, `web`, `windows`, `linux` and
`android` targets when their runtime/tool packs are installed.

Linux builds use `obj/linux`, `bin/linux` and `native/build/linux-x64`. Windows
keeps its existing paths, so both SDKs can build the same checkout independently.

```sh
# Managed, native graphics, packaging and browser parity tests:
bash scripts/test-linux.sh
# Also verify available sibling apps and relocated ELF exports:
AQUARIUS_TEST_PROJECTS=1 bash scripts/test-linux.sh
# Rerun selected siblings after building:
python3 scripts/test-linux-projects.py --executables --only marble painter snake
```

Headless CI uses `xvfb-run -a bash scripts/test-linux.sh` with Mesa drivers.
Reports and captures go to `.web-build/linux-projects`; sibling sources are read
only. Hardware camera and Windows message-injection tests retain their requirements.
