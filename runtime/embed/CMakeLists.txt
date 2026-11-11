# One native library output (docs/native-libraries.md): the application's wasm2c translation, the ahead-of-time glue
# (aot_app.c), the wasm2c runtime and exports.c, compiled once into the object library `parts` and linked with the
# pack's libaquarius_embed.a (and the default libraries the application needs) into each kind asked for. Configured by
# AquariusBuild (NativeLibraryOutput) with a toolchain file from the SDK and:
#   AQUA_AOT_SOURCE, AQUA_AOT_DIGEST, AQUA_WASM2C, AQUA_WASM2C_INCLUDE   as for aot/CMakeLists.txt
#   AQUA_EMBED_TABLE       a folder holding exports.def, the library's C header and symbols.txt (one export per line)
#   AQUA_EMBED_HEADER      the header's file name;  AQUA_EMBED_STATIC  its <NAME>_STATIC macro
#   AQUA_EMBED_PREFIX      the C symbol prefix;     AQUA_EMBED_MODULE  the application .wasm, embedded in the library
#   AQUA_EMBED_LIBRARIES   the default native libraries to link (each a libraries/<name>/link.cmake of this pack)
#   AQUA_EMBED_SHARED_OUTPUT, AQUA_EMBED_STATIC_OUTPUT, AQUA_EMBED_OBJECT_OUTPUT   the files of the kinds to build
#   AQUA_EMBED_JAVA_CLASS, AQUA_EMBED_JDK   the Java binding's class (adds jni.c) and, on the desktops, the JDK
# and what the platform's link rules (platform.cmake, from runtime/platform/<os>/embed.cmake) read: AQUA_EMBED_SONAME,
# AQUA_EMBED_IMPORT_<KIND> (an import library to write). The rules make the targets of the kinds; this file writes
# AQUA_EMBED_TABLE/link.txt: the system libraries a program linking a static or object output needs.
cmake_minimum_required(VERSION 3.20)
project(aquarius_embed_library C CXX)
set(CMAKE_C_STANDARD 17)
get_filename_component(source_directory "${AQUA_AOT_SOURCE}" DIRECTORY)
get_filename_component(source_name "${AQUA_AOT_SOURCE}" NAME)
add_library(parts OBJECT "${AQUA_AOT_SOURCE}.c" aot_app.c "${AQUA_WASM2C}/wasm-rt-impl.c" "${AQUA_WASM2C}/wasm-rt-mem-impl.c"
    "${AQUA_WASM2C}/wasm-rt-exceptions-impl.c" exports.c)
target_include_directories(parts PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}" "${AQUA_EMBED_TABLE}" "${AQUA_WASM2C}" "${AQUA_WASM2C_INCLUDE}"
    "${source_directory}")
# Linear memory is allocated with malloc and checked explicitly: a library installs no signal handlers in its host
# process (guard pages would need them).
target_compile_definitions(parts PRIVATE AQUA_EMBED "AQUA_AOT_HEADER=\"${source_name}.h\"" "AQUA_AOT_DIGEST=\"${AQUA_AOT_DIGEST}\""
    WASM_RT_NONCONFORMING_UNCHECKED_STACK_EXHAUSTION=1 WASM_RT_USE_MMAP=0 WASM_RT_MEMCHECK_BOUNDS_CHECK=1
    "AQUA_EMBED_PREFIX=${AQUA_EMBED_PREFIX}" "AQUA_EMBED_HEADER=\"${AQUA_EMBED_HEADER}\"" "AQUA_EMBED_MODULE=\"${AQUA_EMBED_MODULE}\""
    "${AQUA_EMBED_STATIC}")
# Translated code is machine-written: warnings about it would be noise.
set_source_files_properties("${AQUA_AOT_SOURCE}.c" "${AQUA_WASM2C}/wasm-rt-impl.c" "${AQUA_WASM2C}/wasm-rt-mem-impl.c"
    "${AQUA_WASM2C}/wasm-rt-exceptions-impl.c" PROPERTIES COMPILE_OPTIONS "-w")
# The default libraries register through exports.c (X(name) entries; embed.c registers them before the first start).
set(embed_entries "")
foreach(name IN LISTS AQUA_EMBED_LIBRARIES)
    string(APPEND embed_entries " X(${name})")
endforeach()
target_compile_definitions(parts PRIVATE "AQUA_EMBED_DEFAULT_LIBRARIES=${embed_entries}")
target_compile_options(parts PRIVATE -O2)
set_target_properties(parts PROPERTIES POSITION_INDEPENDENT_CODE ON C_VISIBILITY_PRESET hidden)

# What links into every kind: the embed runtime, then each default library's archives (their link.cmake sets
# embed_library_archives, embed_library_system, embed_library_cxx, embed_library_runtime and embed_library_licenses);
# C++ libraries bring the C++ runtime. The libraries' runtime files and license folders are listed for aqua build in
# AQUA_EMBED_TABLE/runtime.txt and licenses.txt ("name|folder"), which ship them beside the output.
set(embed_archives "${CMAKE_CURRENT_SOURCE_DIR}/${CMAKE_STATIC_LIBRARY_PREFIX}aquarius_embed${CMAKE_STATIC_LIBRARY_SUFFIX}")
set(embed_system "")
set(embed_cxx OFF)
set(embed_runtime "")
set(embed_licenses "")
foreach(name IN LISTS AQUA_EMBED_LIBRARIES)
    set(embed_library_archives "")
    set(embed_library_system "")
    set(embed_library_cxx OFF)
    set(embed_library_runtime "")
    set(embed_library_licenses "")
    include("${CMAKE_CURRENT_SOURCE_DIR}/libraries/${name}/link.cmake")
    list(APPEND embed_archives ${embed_library_archives})
    list(APPEND embed_system ${embed_library_system})
    list(APPEND embed_runtime ${embed_library_runtime})
    if(EXISTS "${embed_library_licenses}")
        list(APPEND embed_licenses "${name}|${embed_library_licenses}")
    endif()
    if(embed_library_cxx)
        set(embed_cxx ON)
    endif()
endforeach()
string(REPLACE ";" "\n" lines "${embed_runtime}")
file(WRITE "${AQUA_EMBED_TABLE}/runtime.txt" "${lines}\n")
string(REPLACE ";" "\n" lines "${embed_licenses}")
file(WRITE "${AQUA_EMBED_TABLE}/licenses.txt" "${lines}\n")
# The Java binding's native half when the java binding is built (AQUA_EMBED_JAVA_CLASS, the binding's class as JNI names
# it). Android's compiler has the JNI headers; the desktops take them from the JDK (AQUA_EMBED_JDK, with the OS folder
# that holds jni_md.h).
if(AQUA_EMBED_JAVA_CLASS)
    target_sources(parts PRIVATE jni.c)
    target_compile_definitions(parts PRIVATE "AQUA_EMBED_JAVA_CLASS=\"${AQUA_EMBED_JAVA_CLASS}\"")
    if(AQUA_EMBED_JDK)
        file(GLOB embed_jni_platform "${AQUA_EMBED_JDK}/include/*/jni_md.h")
        list(TRANSFORM embed_jni_platform REPLACE "/jni_md.h$" "")
        target_include_directories(parts PRIVATE "${AQUA_EMBED_JDK}/include" ${embed_jni_platform})
    endif()
endif()
file(STRINGS "${AQUA_EMBED_TABLE}/symbols.txt" embed_symbols)
include("${CMAKE_CURRENT_SOURCE_DIR}/platform.cmake")
list(REMOVE_DUPLICATES embed_system)
string(REPLACE ";" "\n" embed_system_lines "${embed_system}")
file(WRITE "${AQUA_EMBED_TABLE}/link.txt" "${embed_system_lines}\n")
