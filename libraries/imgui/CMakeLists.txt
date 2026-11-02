# ImGui: Dear ImGui for AquariusLang (src/), with Dear ImGui itself compiled in from third_party/imgui (pinned release,
# MIT; see its README.md). Builds inside the runtime (a default library) or on its own as an external library:
#   cmake -S . -B build -G Ninja -DCMAKE_TOOLCHAIN_FILE=<runtime/sdk/cmake/toolchains/<target>.cmake>
cmake_minimum_required(VERSION 3.20)
project(aqua_imgui C CXX)
include(AquariusLibrary)

set(imgui_upstream third_party/imgui/imgui.cpp third_party/imgui/imgui_demo.cpp third_party/imgui/imgui_draw.cpp
    third_party/imgui/imgui_tables.cpp third_party/imgui/imgui_widgets.cpp)
# src/aqua_imconfig.h turns assertions into errors and keeps Dear ImGui away from files, the clipboard and the shell.
aqua_add_library(imgui SOURCES src/context.cpp src/widgets.cpp src/drawlist.cpp ${imgui_upstream}
    INCLUDE_DIRECTORIES "${CMAKE_CURRENT_SOURCE_DIR}/third_party/imgui" "${CMAKE_CURRENT_SOURCE_DIR}/src"
    DEFINITIONS "IMGUI_USER_CONFIG=\"aqua_imconfig.h\"")