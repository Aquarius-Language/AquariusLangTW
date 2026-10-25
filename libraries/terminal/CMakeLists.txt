# Terminal / 終端機: child processes on Windows, Linux and Android (unsupported on the web).
# Builds inside the runtime (a default library) or on its own as an external library:
#   cmake -S . -B build -G Ninja -DCMAKE_TOOLCHAIN_FILE=<runtime/sdk/cmake/toolchains/<target>.cmake>
cmake_minimum_required(VERSION 3.20)
project(aqua_terminal C)
include(AquariusLibrary)
aqua_add_library(terminal SOURCES src/terminal.c src/platform/windows.c src/platform/posix.c)
if(AQUA_TARGET STREQUAL "windows-x64")
    # winpthreads linked statically, so the external aqua_terminal.dll needs no libwinpthread-1.dll beside it.
    target_link_libraries(aqua_terminal PRIVATE -l:libwinpthread.a)
elseif(NOT AQUA_TARGET STREQUAL "web" AND NOT ANDROID)
    find_package(Threads REQUIRED)
    target_link_libraries(aqua_terminal PRIVATE Threads::Threads)
endif()
