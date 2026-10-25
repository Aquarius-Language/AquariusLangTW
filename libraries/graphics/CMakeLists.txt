# The graphics library: Processing (繪圖), WGPU, GLM, TextInput, Window and Fonts on one WebGPU renderer, plus the
# Aquarius-source modules Models, PBR and GPUMath (aqua/). Builds inside the runtime (a default library) or on its own
# as an external library:
#   cmake -S . -B build -G Ninja -DCMAKE_TOOLCHAIN_FILE=<runtime/sdk/cmake/toolchains/<target>.cmake>
#
# Native targets render with wgpu-native: the official gfx-rs/wgpu-native release for the target is downloaded below,
# checked against its pinned SHA-256 and linked statically. On the web, webgpu.h is implemented by web-glue/webgpu (the
# same header, taken from the host's archive) and text goes through web-glue/canvas.
cmake_minimum_required(VERSION 3.21)
project(aqua_graphics C)
include(AquariusLibrary)

set(wgpu_version 29.0.1.1)
set(AQUA_GRAPHICS_DOWNLOADS "${CMAKE_CURRENT_BINARY_DIR}/downloads" CACHE PATH
    "Directory for the pinned wgpu-native archives (point several build directories at one to download them once)")

set(sources
    src/graphics.c
    src/common/calls.c src/common/codecs.c src/common/files.c src/common/grapheme.c src/common/hashmap.c
    src/common/matrix.c src/common/records.c src/common/unicode.c
    src/gpu/buffer.c src/gpu/device.c src/gpu/shader.c src/gpu/shaders.c src/gpu/surface.c src/gpu/target.c
    src/gpu/texture.c
    src/processing/canvas.c src/processing/color.c src/processing/drawing.c src/processing/geometry.c
    src/processing/image.c src/processing/input.c src/processing/lifecycle.c src/processing/math.c
    src/processing/module.c src/processing/shapes.c src/processing/shaders.c src/processing/text.c
    src/processing/vector.c
    src/modules/fonts.c src/modules/glm.c src/modules/textinput.c src/modules/wgpu.c src/modules/window.c)
set(include_directories src third_party/stb)
set(link_libraries "")

# The pinned wgpu-native release of each target (the web uses the host's archive for webgpu.h only).
set(wgpu_base "https://github.com/gfx-rs/wgpu-native/releases/download/v${wgpu_version}")
set(wgpu_windows-x64 wgpu-windows-x86_64-gnu-release.zip d471e3614733c1d4ddd61bfd19868356477d0d37bf531bf8c6cb64a7f579bd2a)
set(wgpu_linux-x64 wgpu-linux-x86_64-release.zip 95a4d90c071005a98d03eab348beaa6b07e16eb00d1dcdb9f8348f75eb97ec5a)
set(wgpu_android-arm64-v8a wgpu-android-aarch64-release.zip 721741f1b05a20c1738166bedf7a5efb2ba4b382da689526d3fc33de22bdd573)
set(wgpu_android-x86_64 wgpu-android-x86_64-release.zip ef16fc0644bf0e308a39ac4516742da8e22d8c201d3a542cc5baf533d272c491)

if(AQUA_TARGET STREQUAL "web")
    set(wgpu_release "${AQUA_HOST}")
    list(APPEND include_directories "${AQUA_REPOSITORY}/web-glue/webgpu/include")
    list(APPEND sources src/platform/web.c)
else()
    set(wgpu_release "${AQUA_TARGET}")
    if(AQUA_TARGET STREQUAL "windows-x64")
        list(APPEND sources src/platform/windows.c)
        # wgpu-native's Rust standard library and its D3D12/Vulkan/GL backends, then the text layer (GDI).
        list(APPEND link_libraries ws2_32 userenv bcrypt ntdll opengl32 d3dcompiler_47 dxgi d3d12 gdi32 user32 ole32
            oleaut32 propsys runtimeobject advapi32 kernel32 synchronization)
    elseif(AQUA_TARGET STREQUAL "linux-x64")
        list(APPEND sources src/platform/linux.c)
        list(APPEND link_libraries dl pthread m)
    elseif(AQUA_TARGET MATCHES "^android-")
        list(APPEND sources src/platform/android.c src/platform/android_vulkan.c)
        list(APPEND link_libraries android log dl m)
    endif()
endif()
if(NOT DEFINED "wgpu_${wgpu_release}")
    message(FATAL_ERROR "graphics: no pinned wgpu-native release for ${wgpu_release}.")
endif()
list(GET "wgpu_${wgpu_release}" 0 wgpu_file)
list(GET "wgpu_${wgpu_release}" 1 wgpu_sha256)

# Downloads the archive once into AQUA_GRAPHICS_DOWNLOADS and unpacks it. The pinned SHA-256 authenticates the file
# (the toolchain's CMake may lack a CA bundle, as for the other libraries' downloads).
set(archive "${AQUA_GRAPHICS_DOWNLOADS}/${wgpu_file}")
if(EXISTS "${archive}")
    file(SHA256 "${archive}" actual)
    if(NOT actual STREQUAL wgpu_sha256)
        file(REMOVE "${archive}")
    endif()
endif()
if(NOT EXISTS "${archive}")
    message(STATUS "graphics: downloading ${wgpu_base}/${wgpu_file}")
    file(DOWNLOAD "${wgpu_base}/${wgpu_file}" "${archive}.part" EXPECTED_HASH SHA256=${wgpu_sha256} STATUS status)
    list(GET status 0 code)
    if(code)
        file(REMOVE "${archive}.part")
        message(FATAL_ERROR "graphics: download of ${wgpu_base}/${wgpu_file} failed: ${status}")
    endif()
    file(RENAME "${archive}.part" "${archive}")
endif()
set(wgpu_dir "${CMAKE_CURRENT_BINARY_DIR}/wgpu-native-${wgpu_version}-${wgpu_release}")
if(NOT EXISTS "${wgpu_dir}/.extracted")
    file(REMOVE_RECURSE "${wgpu_dir}")
    file(ARCHIVE_EXTRACT INPUT "${archive}" DESTINATION "${wgpu_dir}" PATTERNS "include/*" "lib/libwgpu_native.a")
    file(WRITE "${wgpu_dir}/.extracted" "${archive}\n")
endif()
list(APPEND include_directories "${wgpu_dir}/include")
if(NOT AQUA_TARGET STREQUAL "web")
    # wgpu-native and Wasmtime are both Rust static libraries carrying the Rust standard library; its one unmangled
    # symbol, rust_eh_personality, is renamed in wgpu-native's copy so the runtime links both.
    set(wgpu_library "${wgpu_dir}/lib/libwgpu_native_aqua.a")
    if(NOT EXISTS "${wgpu_library}")
        get_filename_component(compiler_dir "${CMAKE_C_COMPILER}" DIRECTORY)
        find_program(AQUA_GRAPHICS_OBJCOPY NAMES llvm-objcopy HINTS "${compiler_dir}" REQUIRED)
        execute_process(COMMAND "${AQUA_GRAPHICS_OBJCOPY}" --redefine-sym rust_eh_personality=aqua_graphics_rust_eh_personality
            "${wgpu_dir}/lib/libwgpu_native.a" "${wgpu_library}.part" RESULT_VARIABLE failed)
        if(failed)
            message(FATAL_ERROR "graphics: renaming wgpu-native's rust_eh_personality failed.")
        endif()
        file(RENAME "${wgpu_library}.part" "${wgpu_library}")
    endif()
    list(PREPEND link_libraries "${wgpu_library}")
endif()

aqua_add_library(graphics SOURCES ${sources} INCLUDE_DIRECTORIES ${include_directories} LINK_LIBRARIES ${link_libraries})
if(AQUA_TARGET MATCHES "^android-")
    # The emulator's Vulkan driver needs complete timeline values in vkQueueSubmit: wgpu-native's lookup of
    # vkGetInstanceProcAddr is redirected to src/platform/android_vulkan.c (every other dlsym passes through).
    target_link_options(aqua_graphics PUBLIC "-Wl,--wrap=dlsym")
endif()
# Matrix and color math follows System.Numerics' single-precision results: no fused multiply-adds.
target_compile_options(aqua_graphics PRIVATE -ffp-contract=off)
