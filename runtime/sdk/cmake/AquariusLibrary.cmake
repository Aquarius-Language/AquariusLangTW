# aqua_add_library(<name> SOURCES <files...> [INCLUDE_DIRECTORIES <dirs...>] [LINK_LIBRARIES <libs...>]
#                  [DEFINITIONS <defs...>] [NATIVE_SOURCES <files...>] [WEB_SOURCES <files...>])
#
# Builds an AquariusLang library from one source tree for the current target (AQUA_TARGET, set by the toolchain file):
#   - Default libraries, inside the runtime build (AQUA_RUNTIME_BUILD): a static library the native runtime links and
#     registers under <name> (AQUA_LIBRARY(<name>) in the sources); on the web, a side module the host loads.
#   - External libraries (everything else): a shared library: aqua_<name>.dll / libaqua_<name>.so on native targets,
#     aqua_<name>.wasm (a side module sharing the application's memory) on the web.
# NATIVE_SOURCES and WEB_SOURCES are added only for those targets; prefer preprocessor branches on AQUA_OS_* inside
# shared files when the code differs only in a few calls.
include_guard(GLOBAL)
if(AQUA_TARGET STREQUAL "web")
    # CMake's Generic platform resets these after the toolchain file: side modules are shared libraries named <name>.wasm.
    set_property(GLOBAL PROPERTY TARGET_SUPPORTS_SHARED_LIBS TRUE)
    set(CMAKE_SHARED_LIBRARY_CREATE_C_FLAGS "-shared")
    set(CMAKE_SHARED_LIBRARY_CREATE_CXX_FLAGS "-shared")
    set(CMAKE_SHARED_LIBRARY_PREFIX "")
    set(CMAKE_SHARED_LIBRARY_SUFFIX ".wasm")
endif()

function(aqua_add_library name)
    cmake_parse_arguments(ARG "" "" "SOURCES;INCLUDE_DIRECTORIES;LINK_LIBRARIES;DEFINITIONS;NATIVE_SOURCES;WEB_SOURCES" ${ARGN})
    set(target aqua_${name})
    set(sources ${ARG_SOURCES})
    if(AQUA_TARGET STREQUAL "web")
        list(APPEND sources ${ARG_WEB_SOURCES} "${AQUA_SDK}/src/aqua_api_web.c")
        add_library(${target} SHARED ${sources})
        target_compile_definitions(${target} PRIVATE AQUA_LIBRARY_EXTERNAL)
        set_target_properties(${target} PROPERTIES PREFIX "" SUFFIX ".wasm" OUTPUT_NAME "${name}")
        if(AQUA_RUNTIME_BUILD)
            set_property(GLOBAL APPEND PROPERTY AQUA_WEB_LIBRARIES ${target})
        endif()
    elseif(AQUA_RUNTIME_BUILD)
        list(APPEND sources ${ARG_NATIVE_SOURCES})
        add_library(${target} STATIC ${sources})
        set_property(GLOBAL APPEND PROPERTY AQUA_DEFAULT_LIBRARIES ${name})
    else()
        list(APPEND sources ${ARG_NATIVE_SOURCES} "${AQUA_SDK}/src/aqua_api_stub.c")
        add_library(${target} SHARED ${sources})
        target_compile_definitions(${target} PRIVATE AQUA_LIBRARY_EXTERNAL)
        set_target_properties(${target} PROPERTIES PREFIX "" OUTPUT_NAME "aqua_${name}")
        if(NOT WIN32)
            set_target_properties(${target} PROPERTIES PREFIX "lib")
        endif()
    endif()
    target_include_directories(${target} PRIVATE "${AQUA_SDK}/include" ${ARG_INCLUDE_DIRECTORIES})
    target_compile_definitions(${target} PRIVATE AQUA_LIBRARY_NAME=${name} ${ARG_DEFINITIONS})
    target_link_libraries(${target} PRIVATE ${ARG_LINK_LIBRARIES})
    set_target_properties(${target} PROPERTIES C_STANDARD 17 C_STANDARD_REQUIRED ON CXX_STANDARD 20 CXX_STANDARD_REQUIRED ON
        C_VISIBILITY_PRESET hidden CXX_VISIBILITY_PRESET hidden POSITION_INDEPENDENT_CODE ON)
endfunction()
