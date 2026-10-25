# Builds inside the runtime (a default library under libraries/) or on its own as an external library:
#   cmake -S . -B build -G Ninja -DCMAKE_TOOLCHAIN_FILE=<runtime/sdk/cmake/toolchains/<target>.cmake>
cmake_minimum_required(VERSION 3.20)
project(aqua_sample C)
include(AquariusLibrary)
aqua_add_library(sample SOURCES src/sample.c)
