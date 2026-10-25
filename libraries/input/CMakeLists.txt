# AndroidInput / Android輸入: touches, back gesture, insets, soft keyboard and vibration on Android (IsSupported() is
# false on every other target). Builds inside the runtime (a default library) or on its own as an external library:
#   cmake -S . -B build -G Ninja -DCMAKE_TOOLCHAIN_FILE=<runtime/sdk/cmake/toolchains/<target>.cmake>
cmake_minimum_required(VERSION 3.20)
project(aqua_input C)
include(AquariusLibrary)
aqua_add_library(input SOURCES src/input.c)
