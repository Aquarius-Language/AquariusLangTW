# Interaction / 互動: native-feeling input (gestures, context menus, back, shortcuts, platform conventions) for any
# graphics library, plus the Aquarius modules built on it (aqua/). Builds inside the runtime (a default library) or on
# its own as an external library:
#   cmake -S . -B build -G Ninja -DCMAKE_TOOLCHAIN_FILE=<runtime/sdk/cmake/toolchains/<target>.cmake>
# The OS part is src/platform/<os>.c, where <os> is the target's name before its first "-" (windows, linux, android,
# web, ...): a new target adds its file and nothing else here.
cmake_minimum_required(VERSION 3.20)
project(aqua_interaction C)
include(AquariusLibrary)

string(REGEX REPLACE "-.*" "" interaction_os "${AQUA_TARGET}")
set(interaction_libraries)
if(interaction_os STREQUAL "windows")
    set(interaction_libraries user32 advapi32)
elseif(NOT interaction_os STREQUAL "web")
    set(interaction_libraries m)
endif()
aqua_add_library(interaction SOURCES src/interaction.c src/recognizer.c src/platform/${interaction_os}.c
    LINK_LIBRARIES ${interaction_libraries})
