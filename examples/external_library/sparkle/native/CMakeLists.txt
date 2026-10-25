# The native part of the sparkle library: aqua_sparkle.dll / libaqua_sparkle.so / sparkle.wasm, built for one target
# by `aqua library build` with the SDK's toolchain file (or inside a runtime build if it were a default library).
cmake_minimum_required(VERSION 3.20)
project(aqua_sparkle C)
include(AquariusLibrary)
aqua_add_library(sparkle SOURCES src/sparkle.c)
