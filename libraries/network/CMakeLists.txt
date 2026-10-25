# The Network|網路 library. Builds inside the runtime (a default library under libraries/) or on its own as an external
# library:  cmake -S . -B build -G Ninja -DCMAKE_TOOLCHAIN_FILE=<runtime/sdk/cmake/toolchains/<target>.cmake>
# Targets with sockets compile src/native, src/platform and Mbed TLS (pinned below); the web compiles src/web, which
# reaches fetch and WebSocket through web-glue/network.
cmake_minimum_required(VERSION 3.20)
project(aqua_network C)
include(AquariusLibrary)

# The library is one translation unit per kind of target (src/network_native.c, src/network_web.c), so all of its
# functions are static and only the entry point is visible; Mbed TLS keeps its own files.
set(network_native src/network_native.c)
set(network_includes "")
set(network_definitions "")
set(network_libraries "")

if(NOT AQUA_TARGET STREQUAL "web")
    # Mbed TLS 3.6 LTS from its official release, checksum-pinned; compiled into this library with the configuration in
    # third_party/mbedtls and its symbols renamed (aqua_mbedtls_names.h) so no other copy can clash with it.
    include(FetchContent)
    if(POLICY CMP0135)
        cmake_policy(SET CMP0135 NEW)
    endif()
    FetchContent_Declare(aqua_network_mbedtls
        URL https://github.com/Mbed-TLS/mbedtls/releases/download/mbedtls-3.6.7/mbedtls-3.6.7.tar.bz2
        URL_HASH SHA256=a7e8bcbec0e6f761b4af24f25677626b35f762f68eef79c08677a363212d11f6
        SOURCE_SUBDIR not-built-by-its-own-cmake)
    FetchContent_MakeAvailable(aqua_network_mbedtls)
    file(GLOB mbedtls_sources "${aqua_network_mbedtls_SOURCE_DIR}/library/*.c")
    list(APPEND network_native ${mbedtls_sources})
    list(APPEND network_includes "${aqua_network_mbedtls_SOURCE_DIR}/include" "${aqua_network_mbedtls_SOURCE_DIR}/library"
        "${CMAKE_CURRENT_SOURCE_DIR}/third_party/mbedtls")
    list(APPEND network_definitions "MBEDTLS_CONFIG_FILE=\"aqua_mbedtls_config.h\"")
    if(AQUA_TARGET STREQUAL "windows-x64")
        list(APPEND network_libraries ws2_32 crypt32 bcrypt)
        if(AQUA_RUNTIME_BUILD)
            list(APPEND network_libraries pthread)
        else()
            # winpthreads linked statically: an external aqua_network.dll needs no libwinpthread-1.dll beside it.
            list(APPEND network_libraries -Wl,-Bstatic pthread -Wl,-Bdynamic)
        endif()
    elseif(NOT AQUA_TARGET MATCHES "^android-")
        find_package(Threads REQUIRED)
        list(APPEND network_libraries Threads::Threads)
    endif()
endif()

aqua_add_library(network NATIVE_SOURCES ${network_native} WEB_SOURCES src/network_web.c
    INCLUDE_DIRECTORIES ${network_includes} DEFINITIONS ${network_definitions} LINK_LIBRARIES ${network_libraries})
