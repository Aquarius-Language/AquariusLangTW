# Clay: Nic Barker's Clay for AquariusLang (src/clay.c), with Clay itself (the single header third_party/clay/clay.h,
# pinned release, zlib license; see its README.md) compiled in. Builds inside the runtime (a default library) or on its
# own as an external library:
#   cmake -S . -B build -G Ninja -DCMAKE_TOOLCHAIN_FILE=<runtime/sdk/cmake/toolchains/<target>.cmake>
cmake_minimum_required(VERSION 3.20)
project(aqua_clay C)
include(AquariusLibrary)

string(REGEX REPLACE "-.*" "" clay_os "${AQUA_TARGET}")
set(clay_libraries)
if(NOT clay_os STREQUAL "web" AND NOT clay_os STREQUAL "windows")
    set(clay_libraries m)
endif()
aqua_add_library(clay SOURCES src/clay.c INCLUDE_DIRECTORIES "${CMAKE_CURRENT_SOURCE_DIR}/third_party/clay" LINK_LIBRARIES ${clay_libraries})