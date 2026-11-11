# The AquariusLang runtime for one target (choose it with a toolchain file from sdk/cmake/toolchains, or a preset in
# CMakePresets.json): the host core, the target's platform layer and every default library under ../libraries. What the
# target builds from them is its platform layer's platform/<AQUA_OS>/platform.cmake:
#   windows, linux     aquarius(.exe): runs .wasm applications and is the template of standalone executables
#                      (both through platform/desktop/desktop.cmake)
#   android            libaquarius.so: the native activity of Android applications
#   web                aquarius.wasm plus one side module per library, loaded by ../web-glue
# A platform layer with an embed.cmake also gets embed/ (embed/runtime.cmake): the runtime native library outputs
# link (docs/native-libraries.md).
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
get_filename_component(default_library_root "${CMAKE_CURRENT_SOURCE_DIR}/../libraries" ABSOLUTE)
set(AQUA_LIBRARY_ROOT "${default_library_root}" CACHE PATH "Folder holding the default libraries")
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

set(host_sources host/api.c host/application.c host/calls.c host/core.c host/format.c host/guest.c host/image.c
    host/promise.c host/registry.c host/scheduler.c host/session.c host/values.c host/window_object.c)

# The default libraries the host registers at startup, as X(name) entries for host/session.c.
get_property(default_libraries GLOBAL PROPERTY AQUA_DEFAULT_LIBRARIES)
set(default_entries "")
foreach(name IN LISTS default_libraries)
    string(APPEND default_entries " X(${name})")
endforeach()
file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/generated/default_libraries.h"
    "/* The default libraries of this runtime build (runtime/CMakeLists.txt). */\n#define AQUA_DEFAULT_LIBRARIES${default_entries}\n")

# The target's platform layer builds the runtime from host_sources, default_libraries and the generated header.
set(platform_build "${CMAKE_CURRENT_SOURCE_DIR}/platform/${AQUA_OS}/platform.cmake")
if(NOT EXISTS "${platform_build}")
    message(FATAL_ERROR "${AQUA_TARGET} has no platform layer: add runtime/platform/${AQUA_OS}/platform.cmake (docs/adding-a-target.md).")
endif()
include("${platform_build}")

# Native library outputs (docs/native-libraries.md): a platform layer with link rules for them (embed.cmake) gets the
# embed runtime in its pack.
if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/platform/${AQUA_OS}/embed.cmake")
    include(embed/runtime.cmake)
endif()
