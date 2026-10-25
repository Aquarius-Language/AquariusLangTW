# The test-only library of web-glue/webgpu (README.md): webgpu.h code built against wgpu-native on native targets and
# against the browser's WebGPU (this glue domain) on the web, as an external library:
#   cmake -S web-glue/webgpu/tests -B .native-build/lib-webgpu/<target> -G Ninja -DCMAKE_TOOLCHAIN_FILE=<runtime/sdk/cmake/toolchains/<target>.cmake>
# Windows: aqua_webgpu_test.dll (aquarius --library), web: webgpu_test.wasm (web-run.mjs --libraries webgpu_test).
cmake_minimum_required(VERSION 3.20)
project(aqua_webgpu_test C)
include(AquariusLibrary)

if(AQUA_TARGET STREQUAL "web")
    # webgpu.h is the same file in every target's wgpu-native SDK: the web uses the host's copy, plus webgpu_web.h.
    aqua_sdk_directory(wgpu ${AQUA_HOST} wgpu_sdk)
    aqua_add_library(webgpu_test SOURCES src/webgpu_test.c
        INCLUDE_DIRECTORIES "${wgpu_sdk}/include" "${CMAKE_CURRENT_SOURCE_DIR}/../include")
else()
    aqua_sdk_directory(wgpu ${AQUA_TARGET} wgpu_sdk)
    # wgpu-native's static library and the system libraries it uses.
    if(AQUA_TARGET STREQUAL "windows-x64")
        set(wgpu_system d3dcompiler ws2_32 userenv bcrypt ntdll opengl32 gdi32 user32 kernel32 advapi32 ole32 oleaut32 propsys runtimeobject)
    elseif(AQUA_TARGET MATCHES "^android-")
        set(wgpu_system android log m dl)
    else()
        set(wgpu_system m dl pthread)
    endif()
    aqua_add_library(webgpu_test SOURCES src/webgpu_test.c INCLUDE_DIRECTORIES "${wgpu_sdk}/include"
        LINK_LIBRARIES "${wgpu_sdk}/lib/libwgpu_native.a" ${wgpu_system})
endif()
