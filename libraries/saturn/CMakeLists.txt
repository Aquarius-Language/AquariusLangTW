# Saturn / 土星: Yoga layout trees, styles, text, painting and hit testing (src/), with Yoga itself compiled in from
# third_party/yoga (pinned upstream source, MIT; see its README.md). Builds inside the runtime (a default library) or on
# its own as an external library:
#   cmake -S . -B build -G Ninja -DCMAKE_TOOLCHAIN_FILE=<runtime/sdk/cmake/toolchains/<target>.cmake>
cmake_minimum_required(VERSION 3.20)
project(aqua_saturn C CXX)
include(AquariusLibrary)

file(GLOB_RECURSE saturn_yoga_sources CONFIGURE_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/third_party/yoga/yoga/*.cpp")
# YG_EXPORT= keeps Yoga's C API out of the library's exported symbols (third_party/yoga/README.md).
aqua_add_library(saturn SOURCES src/saturn.cpp src/style.cpp src/text.cpp src/paint.cpp ${saturn_yoga_sources}
    INCLUDE_DIRECTORIES "${CMAKE_CURRENT_SOURCE_DIR}/third_party/yoga"
    DEFINITIONS YG_EXPORT=)