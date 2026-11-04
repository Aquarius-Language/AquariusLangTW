# RemoteWindow / 遠端視窗: the remote window protocol (aqua/remote_window.h) for scripts on every target: a viewer for
# the windows of an application started with AQUARIUS_REMOTE_WINDOW, a relay for servers between the two, and a
# sender. Builds inside the runtime (a default library) or on its own as an external library:
#   cmake -S . -B build -G Ninja -DCMAKE_TOOLCHAIN_FILE=<runtime/sdk/cmake/toolchains/<target>.cmake>
cmake_minimum_required(VERSION 3.20)
project(aqua_remotewindow C)
include(AquariusLibrary)
aqua_add_library(remotewindow SOURCES src/remotewindow.c)
if(NOT AQUA_TARGET STREQUAL "web" AND NOT WIN32)
    target_link_libraries(aqua_remotewindow PRIVATE m)
endif()
