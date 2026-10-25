# Builds inside the runtime (a default library under libraries/) or on its own as an external library:
#   cmake -S . -B build -G Ninja -DCMAKE_TOOLCHAIN_FILE=<runtime/sdk/cmake/toolchains/<target>.cmake>
# Desktop OpenGL 3.3 core only: other targets skip the library.
cmake_minimum_required(VERSION 3.20)
project(aqua_opengl C)
include(AquariusLibrary)
aqua_add_library(opengl PLATFORMS windows linux
    SOURCES src/opengl.c src/glfw.c src/gl.c src/image.c src/files.c)
