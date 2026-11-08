# An iOS application's executable (aqua build --target ios; CLAUDE.md rule 11's ahead-of-time path, the only one iOS
# allows): the application's wasm2c translation with aot_app.c and the wasm2c runtime, linked with the pack's
# libaquarius.a and the frameworks it lists. Configured by AquariusBuild with the target's toolchain file and:
#   AQUA_AOT_SOURCE  the application's wasm2c translation without extension (<name>.c and <name>.h, module "app")
#   AQUA_AOT_DIGEST  the application module's SHA-256 (64 hex digits)
#   AQUA_WASM2C      the pinned WABT's wasm2c runtime folder (share/wabt/wasm2c) and AQUA_WASM2C_INCLUDE its include/
#   AQUA_AOT_OUTPUT  the executable to write (<Name>.app/<Name>)
# The pack installs this file with aot_app.c, aot_app.h and guest_exports.h beside it.
cmake_minimum_required(VERSION 3.20)
project(aquarius_ios_app C)
set(CMAKE_C_STANDARD 17)
get_filename_component(pack "${CMAKE_CURRENT_SOURCE_DIR}/.." ABSOLUTE)
get_filename_component(source_directory "${AQUA_AOT_SOURCE}" DIRECTORY)
get_filename_component(source_name "${AQUA_AOT_SOURCE}" NAME)
add_executable(application "${AQUA_AOT_SOURCE}.c" "${CMAKE_CURRENT_SOURCE_DIR}/aot_app.c"
    "${AQUA_WASM2C}/wasm-rt-impl.c" "${AQUA_WASM2C}/wasm-rt-mem-impl.c" "${AQUA_WASM2C}/wasm-rt-exceptions-impl.c")
target_include_directories(application PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}" "${AQUA_WASM2C}" "${AQUA_WASM2C_INCLUDE}" "${source_directory}")
target_compile_definitions(application PRIVATE "AQUA_AOT_HEADER=\"${source_name}.h\"" "AQUA_AOT_DIGEST=\"${AQUA_AOT_DIGEST}\""
    WASM_RT_NONCONFORMING_UNCHECKED_STACK_EXHAUSTION=1)
target_compile_options(application PRIVATE -O2 -w)
file(STRINGS "${pack}/frameworks.txt" frameworks)
set(framework_options "")
foreach(framework IN LISTS frameworks)
    if(framework)
        list(APPEND framework_options "SHELL:-framework ${framework}")
    endif()
endforeach()
# libaquarius.a holds main() (the UIKit entry) and every default library's registration, kept with -force_load.
target_link_options(application PRIVATE "SHELL:-Wl,-force_load,${pack}/libaquarius.a" ${framework_options} -lc++ -lm)
get_filename_component(output_directory "${AQUA_AOT_OUTPUT}" DIRECTORY)
get_filename_component(output_name "${AQUA_AOT_OUTPUT}" NAME)
# The executable alone: the compiler assembles and signs the bundle around it.
set_target_properties(application PROPERTIES OUTPUT_NAME "${output_name}" RUNTIME_OUTPUT_DIRECTORY "${output_directory}" MACOSX_BUNDLE OFF)
