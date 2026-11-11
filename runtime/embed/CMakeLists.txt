# One native library output (docs/native-libraries.md): the application's wasm2c translation, the ahead-of-time glue
# (aot_app.c), the wasm2c runtime and exports.c, linked with the pack's libaquarius_embed.a into a shared library that
# exports only the export table's functions. Configured by AquariusBuild (NativeLibraryOutput) with a toolchain file
# from the SDK and:
#   AQUA_AOT_SOURCE, AQUA_AOT_DIGEST, AQUA_WASM2C, AQUA_WASM2C_INCLUDE   as for aot/CMakeLists.txt
#   AQUA_AOT_OUTPUT        the library file to write
#   AQUA_EMBED_TABLE       a folder holding exports.def, the library's C header and symbols.txt (one export per line)
#   AQUA_EMBED_HEADER      the header's file name;  AQUA_EMBED_STATIC  its <NAME>_STATIC macro
#   AQUA_EMBED_PREFIX      the C symbol prefix;     AQUA_EMBED_MODULE  the application .wasm, embedded in the library
# and what the platform's link rules (platform.cmake, from runtime/platform/<os>/embed.cmake) read: AQUA_EMBED_SONAME,
# AQUA_EMBED_IMPORT_<KIND> (an import library to write).
cmake_minimum_required(VERSION 3.20)
project(aquarius_embed_library C)
set(CMAKE_C_STANDARD 17)
get_filename_component(source_directory "${AQUA_AOT_SOURCE}" DIRECTORY)
get_filename_component(source_name "${AQUA_AOT_SOURCE}" NAME)
add_library(library SHARED "${AQUA_AOT_SOURCE}.c" aot_app.c "${AQUA_WASM2C}/wasm-rt-impl.c" "${AQUA_WASM2C}/wasm-rt-mem-impl.c"
    "${AQUA_WASM2C}/wasm-rt-exceptions-impl.c" exports.c)
target_include_directories(library PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}" "${AQUA_EMBED_TABLE}" "${AQUA_WASM2C}" "${AQUA_WASM2C_INCLUDE}"
    "${source_directory}")
# Linear memory is allocated with malloc and checked explicitly: a library installs no signal handlers in its host
# process (guard pages would need them).
target_compile_definitions(library PRIVATE AQUA_EMBED "AQUA_AOT_HEADER=\"${source_name}.h\"" "AQUA_AOT_DIGEST=\"${AQUA_AOT_DIGEST}\""
    WASM_RT_NONCONFORMING_UNCHECKED_STACK_EXHAUSTION=1 WASM_RT_USE_MMAP=0 WASM_RT_MEMCHECK_BOUNDS_CHECK=1
    "AQUA_EMBED_PREFIX=${AQUA_EMBED_PREFIX}" "AQUA_EMBED_HEADER=\"${AQUA_EMBED_HEADER}\"" "AQUA_EMBED_MODULE=\"${AQUA_EMBED_MODULE}\""
    "${AQUA_EMBED_STATIC}")
# Translated code is machine-written: warnings about it would be noise.
set_source_files_properties("${AQUA_AOT_SOURCE}.c" "${AQUA_WASM2C}/wasm-rt-impl.c" "${AQUA_WASM2C}/wasm-rt-mem-impl.c"
    "${AQUA_WASM2C}/wasm-rt-exceptions-impl.c" PROPERTIES COMPILE_OPTIONS "-w")
target_compile_options(library PRIVATE -O2)
target_link_libraries(library PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/${CMAKE_STATIC_LIBRARY_PREFIX}aquarius_embed${CMAKE_STATIC_LIBRARY_SUFFIX}")
set_target_properties(library PROPERTIES PREFIX "" SUFFIX "" C_VISIBILITY_PRESET hidden)
get_filename_component(output_directory "${AQUA_AOT_OUTPUT}" DIRECTORY)
get_filename_component(output_name "${AQUA_AOT_OUTPUT}" NAME)
set_target_properties(library PROPERTIES OUTPUT_NAME "${output_name}" RUNTIME_OUTPUT_DIRECTORY "${output_directory}"
    LIBRARY_OUTPUT_DIRECTORY "${output_directory}" ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/archive")
file(STRINGS "${AQUA_EMBED_TABLE}/symbols.txt" embed_symbols)
include("${CMAKE_CURRENT_SOURCE_DIR}/platform.cmake")
