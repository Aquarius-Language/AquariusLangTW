# An application's ahead-of-time library (aqua build --optimize; CLAUDE.md rule 11). Configured by AquariusBuild with
# a toolchain file from the SDK (sdk/cmake/toolchains/<target>.cmake) and:
#   AQUA_AOT_SOURCE  the application's wasm2c translation without extension (<name>.c and <name>.h, module "app")
#   AQUA_AOT_DIGEST  the application module's SHA-256 (64 hex digits)
#   AQUA_WASM2C      the pinned WABT's wasm2c runtime folder (share/wabt/wasm2c) and AQUA_WASM2C_INCLUDE its include/
#   AQUA_AOT_OUTPUT  the library to write (<executable>.aot.dll / .aot.so)
# The pack installs this file with aot_app.c, aot_app.h and guest_exports.h beside it.
cmake_minimum_required(VERSION 3.20)
project(aquarius_aot C)
set(CMAKE_C_STANDARD 17)
get_filename_component(source_directory "${AQUA_AOT_SOURCE}" DIRECTORY)
get_filename_component(source_name "${AQUA_AOT_SOURCE}" NAME)
add_library(application SHARED "${AQUA_AOT_SOURCE}.c" "${CMAKE_CURRENT_SOURCE_DIR}/aot_app.c"
    "${AQUA_WASM2C}/wasm-rt-impl.c" "${AQUA_WASM2C}/wasm-rt-mem-impl.c" "${AQUA_WASM2C}/wasm-rt-exceptions-impl.c")
target_include_directories(application PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}" "${AQUA_WASM2C}" "${AQUA_WASM2C_INCLUDE}" "${source_directory}")
target_compile_definitions(application PRIVATE "AQUA_AOT_HEADER=\"${source_name}.h\"" "AQUA_AOT_DIGEST=\"${AQUA_AOT_DIGEST}\""
    WASM_RT_NONCONFORMING_UNCHECKED_STACK_EXHAUSTION=1)
# Translated code is machine-written: warnings about it would be noise.
target_compile_options(application PRIVATE -O2 -w)
set_target_properties(application PROPERTIES PREFIX "" C_VISIBILITY_PRESET hidden)
get_filename_component(output_directory "${AQUA_AOT_OUTPUT}" DIRECTORY)
get_filename_component(output_name "${AQUA_AOT_OUTPUT}" NAME)
set_target_properties(application PROPERTIES OUTPUT_NAME "${output_name}" SUFFIX "" RUNTIME_OUTPUT_DIRECTORY "${output_directory}"
    LIBRARY_OUTPUT_DIRECTORY "${output_directory}")
if(WIN32)
    target_link_options(application PRIVATE -static)
endif()
