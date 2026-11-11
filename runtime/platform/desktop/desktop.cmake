# The runtime of a desktop target, included by its platform/<os>/platform.cmake (windows, linux, macos): GLFW windows and
# the two launchers. The including file sets platform_sources, platform_entry and platform_libraries (its layer's
# sources, its process entry and its system libraries) first, and adds what only its operating system needs (GLFW
# sources, linker options) after. Native library outputs (runtime/embed) link the layer's sources with
# platform/desktop/embed.c instead of the process entry and the launcher.
#
# GLFW 3.4 for desktop windows, checksum-pinned.
include(FetchContent)
if(POLICY CMP0135)
    cmake_policy(SET CMP0135 NEW)
endif()
set(GLFW_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(GLFW_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(GLFW_BUILD_DOCS OFF CACHE BOOL "" FORCE)
set(GLFW_INSTALL OFF CACHE BOOL "" FORCE)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
FetchContent_Declare(glfw URL https://github.com/glfw/glfw/archive/refs/tags/3.4.tar.gz
    URL_HASH SHA256=c038d34200234d071fae9345bc455e4a8f2f544ab60150765d7704e08f3dac01)
FetchContent_MakeAvailable(glfw)
add_library(aqua_window STATIC platform/desktop/window_glfw.c platform/desktop/window_remote.c)
target_include_directories(aqua_window PRIVATE sdk/include)
target_link_libraries(aqua_window PUBLIC glfw)

aqua_sdk_directory(wasmtime ${AQUA_TARGET} wasmtime_sdk)
# Two launchers share every source but the engine: aquarius (Wasmtime: aqua run and every exported app) and
# aquarius-aot (engine_aot.c: an app built with aqua build --optimize, whose code is the application library beside
# it). The aot/ folder holds what aqua build compiles into that library (see aot/CMakeLists.txt). Compiled code recurses
# on the native stack without a per-call depth count (aot_app.c), so the platform layer reserves a large stack for
# aquarius-aot.
set(launcher_sources ${host_sources} host/overlay.c host/sha256.c host/threads.c ${platform_sources} ${platform_entry} platform/desktop/main.c
    platform/desktop/jit.c)
set(embed_platform_sources ${platform_sources} platform/desktop/embed.c)
add_executable(aquarius ${launcher_sources} host/engine_wasmtime.c)
add_executable(aquarius-aot ${launcher_sources} host/engine_aot.c)
target_include_directories(aquarius PRIVATE "${wasmtime_sdk}/include")
target_compile_definitions(aquarius PRIVATE WASM_API_EXTERN= WASI_API_EXTERN=)
target_link_libraries(aquarius PRIVATE "${wasmtime_sdk}/lib/libwasmtime.a")
foreach(launcher IN ITEMS aquarius aquarius-aot)
    target_include_directories(${launcher} PRIVATE sdk/include "${CMAKE_CURRENT_BINARY_DIR}/generated")
    target_compile_definitions(${launcher} PRIVATE AQUA_HAS_DEFAULT_LIBRARIES)
    target_link_libraries(${launcher} PRIVATE aqua_window ${platform_libraries})
    foreach(name IN LISTS default_libraries)
        target_link_libraries(${launcher} PRIVATE aqua_${name})
    endforeach()
endforeach()
install(TARGETS aquarius aquarius-aot RUNTIME DESTINATION .)
install(FILES aot/CMakeLists.txt aot/aot_app.c aot/aot_app.h host/guest_exports.h DESTINATION aot)
