# The AquariusLang runtime for one target (choose it with a toolchain file from sdk/cmake/toolchains, or a preset in
# CMakePresets.json): the host core, the target's platform layer and every default library under ../libraries.
#   windows-x64, linux-x64      aquarius(.exe): runs .wasm applications and is the template of standalone executables
#   android-*                   libaquarius.so: the native activity of Android applications
#   web                         aquarius.wasm plus one side module per library, loaded by ../web-glue
cmake_minimum_required(VERSION 3.20)
project(aquarius C CXX)
if(NOT AQUA_TARGET)
    message(FATAL_ERROR "Configure with a toolchain file from runtime/sdk/cmake/toolchains (or cmake --preset <target>).")
endif()
include(AquariusLibrary)
set(AQUA_RUNTIME_BUILD ON)
set(CMAKE_C_STANDARD 17)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Default libraries: every folder of ../libraries with a CMakeLists.txt, unless AQUA_LIBRARIES names a subset.
get_filename_component(AQUA_LIBRARY_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../libraries" ABSOLUTE)
set(AQUA_LIBRARIES "" CACHE STRING "Default libraries to build (folder names under libraries/); empty builds all")
if(AQUA_LIBRARIES)
    set(library_folders ${AQUA_LIBRARIES})
else()
    file(GLOB library_lists RELATIVE "${AQUA_LIBRARY_ROOT}" "${AQUA_LIBRARY_ROOT}/*/CMakeLists.txt")
    set(library_folders "")
    foreach(list IN LISTS library_lists)
        get_filename_component(folder "${list}" DIRECTORY)
        list(APPEND library_folders "${folder}")
    endforeach()
endif()
foreach(folder IN LISTS library_folders)
    add_subdirectory("${AQUA_LIBRARY_ROOT}/${folder}" "libraries/${folder}")
endforeach()

set(host_sources host/api.c host/application.c host/calls.c host/core.c host/format.c host/guest.c host/registry.c
    host/scheduler.c host/session.c host/values.c)

# The default libraries the host registers at startup, as X(name) entries for host/session.c.
get_property(default_libraries GLOBAL PROPERTY AQUA_DEFAULT_LIBRARIES)
set(default_entries "")
foreach(name IN LISTS default_libraries)
    string(APPEND default_entries " X(${name})")
endforeach()
file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/generated/default_libraries.h"
    "/* The default libraries of this runtime build (runtime/CMakeLists.txt). */\n#define AQUA_DEFAULT_LIBRARIES${default_entries}\n")

if(AQUA_TARGET STREQUAL "windows-x64" OR AQUA_TARGET STREQUAL "linux-x64")
    aqua_sdk_directory(wasmtime ${AQUA_TARGET} wasmtime_sdk)
    if(AQUA_TARGET STREQUAL "windows-x64")
        set(platform_sources platform/windows/platform.c)
        set(platform_libraries ws2_32 userenv ntdll bcrypt ole32 shell32 advapi32 kernel32 synchronization)
    else()
        set(platform_sources platform/linux/platform.c)
        set(platform_libraries pthread dl m)
    endif()
    add_executable(aquarius ${host_sources} host/engine_wasmtime.c host/threads.c ${platform_sources}
        platform/desktop/main.c platform/desktop/sha256.c)
    target_include_directories(aquarius PRIVATE sdk/include "${CMAKE_CURRENT_BINARY_DIR}/generated" "${wasmtime_sdk}/include")
    target_compile_definitions(aquarius PRIVATE AQUA_HAS_DEFAULT_LIBRARIES WASM_API_EXTERN= WASI_API_EXTERN=)
    target_link_libraries(aquarius PRIVATE "${wasmtime_sdk}/lib/libwasmtime.a" ${platform_libraries})
    foreach(name IN LISTS default_libraries)
        target_link_libraries(aquarius PRIVATE aqua_${name})
    endforeach()
    install(TARGETS aquarius RUNTIME DESTINATION .)
endif()
