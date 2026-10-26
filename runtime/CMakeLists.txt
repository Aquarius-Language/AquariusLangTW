# The AquariusLang runtime for one target (choose it with a toolchain file from sdk/cmake/toolchains, or a preset in
# CMakePresets.json): the host core, the target's platform layer and every default library under ../libraries.
#   windows-x64, linux-x64      aquarius(.exe): runs .wasm applications and is the template of standalone executables
#   android-*                   libaquarius.so: the native activity of Android applications
#   web                         aquarius.wasm plus one side module per library, loaded by ../web-glue
cmake_minimum_required(VERSION 3.20)
project(aquarius C CXX)
if(AQUA_TARGET STREQUAL "windows-x64")
    enable_language(RC) # the executable's manifest
endif()
if(NOT AQUA_TARGET)
    message(FATAL_ERROR "Configure with a toolchain file from runtime/sdk/cmake/toolchains (or cmake --preset <target>).")
endif()
include(AquariusLibrary)
set(AQUA_RUNTIME_BUILD ON)
set(CMAKE_C_STANDARD 17)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Default libraries: every folder of ../libraries with a CMakeLists.txt, unless AQUA_LIBRARIES names a subset.
get_filename_component(default_library_root "${CMAKE_CURRENT_SOURCE_DIR}/../libraries" ABSOLUTE)
set(AQUA_LIBRARY_ROOT "${default_library_root}" CACHE PATH "Folder holding the default libraries")
set(AQUA_LIBRARIES "" CACHE STRING "Default libraries to build (folder names under libraries/); empty builds all")
if(AQUA_LIBRARIES)
    set(library_folders ${AQUA_LIBRARIES})
else()
    file(GLOB library_lists RELATIVE "${AQUA_LIBRARY_ROOT}" "${AQUA_LIBRARY_ROOT}/*/CMakeLists.txt")
    set(library_folders "")
    foreach(list IN LISTS library_lists)
        get_filename_component(folder "${list}" DIRECTORY)
        list(APPEND library_folders "${folder}")
    endforeach()
endif()
foreach(folder IN LISTS library_folders)
    add_subdirectory("${AQUA_LIBRARY_ROOT}/${folder}" "libraries/${folder}")
endforeach()

set(host_sources host/api.c host/application.c host/calls.c host/core.c host/format.c host/guest.c host/image.c
    host/registry.c host/scheduler.c host/session.c host/values.c host/window_object.c)

# The default libraries the host registers at startup, as X(name) entries for host/session.c.
get_property(default_libraries GLOBAL PROPERTY AQUA_DEFAULT_LIBRARIES)
set(default_entries "")
foreach(name IN LISTS default_libraries)
    string(APPEND default_entries " X(${name})")
endforeach()
file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/generated/default_libraries.h"
    "/* The default libraries of this runtime build (runtime/CMakeLists.txt). */\n#define AQUA_DEFAULT_LIBRARIES${default_entries}\n")

if(AQUA_TARGET STREQUAL "windows-x64" OR AQUA_TARGET STREQUAL "linux-x64")
    # GLFW 3.4 for desktop windows, checksum-pinned. On Windows its window procedure file is compiled through a wrapper
    # (platform/desktop/glfw_win32_window.c) that keeps the upstream source unchanged.
    include(FetchContent)
    if(POLICY CMP0135)
        cmake_policy(SET CMP0135 NEW)
    endif()
    set(GLFW_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
    set(GLFW_BUILD_TESTS OFF CACHE BOOL "" FORCE)
    set(GLFW_BUILD_DOCS OFF CACHE BOOL "" FORCE)
    set(GLFW_INSTALL OFF CACHE BOOL "" FORCE)
    set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
    FetchContent_Declare(glfw URL https://github.com/glfw/glfw/archive/refs/tags/3.4.tar.gz
        URL_HASH SHA256=c038d34200234d071fae9345bc455e4a8f2f544ab60150765d7704e08f3dac01)
    FetchContent_MakeAvailable(glfw)
    if(WIN32)
        get_target_property(glfw_sources glfw SOURCES)
        list(FILTER glfw_sources EXCLUDE REGEX "(^|/)win32_window\.c$")
        list(APPEND glfw_sources "${CMAKE_CURRENT_SOURCE_DIR}/platform/desktop/glfw_win32_window.c")
        set_property(TARGET glfw PROPERTY SOURCES "${glfw_sources}")
    endif()
    add_library(aqua_window STATIC platform/desktop/window_glfw.c)
    target_include_directories(aqua_window PRIVATE sdk/include)
    target_link_libraries(aqua_window PUBLIC glfw)
    if(WIN32)
        target_link_libraries(aqua_window PUBLIC imm32)
    else()
        if(GLFW_BUILD_X11)
            target_compile_definitions(aqua_window PRIVATE AQUA_GLFW_X11)
        endif()
        if(GLFW_BUILD_WAYLAND)
            target_compile_definitions(aqua_window PRIVATE AQUA_GLFW_WAYLAND)
        endif()
    endif()
    aqua_sdk_directory(wasmtime ${AQUA_TARGET} wasmtime_sdk)
    if(AQUA_TARGET STREQUAL "windows-x64")
        set(platform_sources platform/windows/platform.c platform/windows/aquarius.rc)
        set(platform_libraries ws2_32 userenv ntdll bcrypt ole32 shell32 advapi32 kernel32 synchronization)
    else()
        set(platform_sources platform/linux/platform.c platform/posix/posix.c)
        set(platform_libraries pthread dl m)
    endif()
    add_executable(aquarius ${host_sources} host/engine_wasmtime.c host/sha256.c host/threads.c ${platform_sources}
        platform/desktop/main.c)
    target_include_directories(aquarius PRIVATE sdk/include "${CMAKE_CURRENT_BINARY_DIR}/generated" "${wasmtime_sdk}/include")
    target_compile_definitions(aquarius PRIVATE AQUA_HAS_DEFAULT_LIBRARIES WASM_API_EXTERN= WASI_API_EXTERN=)
    target_link_libraries(aquarius PRIVATE aqua_window "${wasmtime_sdk}/lib/libwasmtime.a" ${platform_libraries})
    foreach(name IN LISTS default_libraries)
        target_link_libraries(aquarius PRIVATE aqua_${name})
    endforeach()
    install(TARGETS aquarius RUNTIME DESTINATION .)
elseif(AQUA_TARGET MATCHES "^android-")
    # The native activity: android_native_app_glue runs android_main on the activity's thread.
    aqua_sdk_directory(wasmtime ${AQUA_TARGET} wasmtime_sdk)
    string(JSON wasmtime_version GET "${AQUA_PINS}" sdks wasmtime version)
    add_library(aquarius SHARED ${host_sources} host/engine_wasmtime.c host/sha256.c host/threads.c platform/android/platform.c
        platform/android/window.c platform/posix/posix.c "${ANDROID_NDK}/sources/android/native_app_glue/android_native_app_glue.c")
    target_include_directories(aquarius PRIVATE sdk/include "${CMAKE_CURRENT_BINARY_DIR}/generated" "${wasmtime_sdk}/include"
        "${ANDROID_NDK}/sources/android/native_app_glue")
    target_compile_definitions(aquarius PRIVATE AQUA_HAS_DEFAULT_LIBRARIES WASM_API_EXTERN= WASI_API_EXTERN= AQUA_WASMTIME_VERSION="${wasmtime_version}")
    # The activity's entry point must stay exported while everything else of the glue stays local.
    target_link_options(aquarius PRIVATE -u ANativeActivity_onCreate)
    target_link_libraries(aquarius PRIVATE "${wasmtime_sdk}/lib/libwasmtime.a" android log m dl)
    foreach(name IN LISTS default_libraries)
        target_link_libraries(aquarius PRIVATE aqua_${name})
    endforeach()
    install(TARGETS aquarius LIBRARY DESTINATION .)

    # The activity (platform/android/java): compiled against the pinned SDK platform with the pinned JDK, dexed for APKs.
    string(JSON android_platform GET "${AQUA_PINS}" androidSdk platform)
    string(JSON android_build_tools GET "${AQUA_PINS}" androidSdk buildTools)
    string(JSON jdk_directory GET "${AQUA_PINS}" jdk directory)
    string(REPLACE "{platform}" "${AQUA_HOST}" jdk_directory "${jdk_directory}")
    set(jdk "${AQUA_TOOLCHAINS}/${jdk_directory}")
    get_filename_component(android_sdk "${AQUA_NDK}/../.." ABSOLUTE)
    if(DEFINED ENV{ANDROID_HOME})
        set(android_sdk "$ENV{ANDROID_HOME}")
    endif()
    set(android_jar "${android_sdk}/platforms/android-${android_platform}/android.jar")
    set(d8 "${android_sdk}/build-tools/${android_build_tools}/d8${AQUA_SCRIPT}")
    foreach(tool IN ITEMS "${jdk}/bin/javac${AQUA_EXE}" "${android_jar}" "${d8}")
        if(NOT EXISTS "${tool}")
            message(FATAL_ERROR "${tool} is missing: run scripts/install-toolchain.ps1 -Targets ${AQUA_TARGET} and install platform ${android_platform} and build-tools ${android_build_tools} with sdkmanager.")
        endif()
    endforeach()
    file(GLOB_RECURSE java_sources CONFIGURE_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/platform/android/java/*.java")
    add_custom_command(OUTPUT classes.dex
        COMMAND "${CMAKE_COMMAND}" -E rm -rf java-classes
        COMMAND "${jdk}/bin/javac${AQUA_EXE}" --release 17 -encoding UTF-8 -Xlint:-options -classpath "${android_jar}" -d java-classes ${java_sources}
        COMMAND "${jdk}/bin/jar${AQUA_EXE}" cf java-classes.jar -C java-classes .
        COMMAND "${CMAKE_COMMAND}" -E env "JAVA_HOME=${jdk}" "${d8}" --release --min-api ${AQUA_ANDROID_API} --lib "${android_jar}" --output . java-classes.jar
        DEPENDS ${java_sources} VERBATIM)
    add_custom_target(aquarius_activity ALL DEPENDS classes.dex)
    install(FILES "${CMAKE_CURRENT_BINARY_DIR}/classes.dex" platform/android/AndroidManifest.xml DESTINATION .)
elseif(AQUA_TARGET STREQUAL "web")
    # The host core as a side module; the glue links it after libc and before the libraries.
    add_library(aquarius SHARED ${host_sources} host/engine_web.c host/threads_web.c platform/web/platform.c platform/web/window.c)
    target_include_directories(aquarius PRIVATE sdk/include "${CMAKE_CURRENT_BINARY_DIR}/generated")
    set_target_properties(aquarius PROPERTIES OUTPUT_NAME aquarius)
    get_property(web_libraries GLOBAL PROPERTY AQUA_WEB_LIBRARIES)
    install(TARGETS aquarius LIBRARY DESTINATION . RUNTIME DESTINATION .)
    if(web_libraries)
        install(TARGETS ${web_libraries} LIBRARY DESTINATION libraries RUNTIME DESTINATION libraries)
    endif()
    # The sysroot's shared C and C++ libraries the side modules link against.
    set(wasi_libraries "${AQUA_WASI_SYSROOT}/lib/wasm32-wasip1")
    install(FILES "${wasi_libraries}/libc.so" "${wasi_libraries}/noeh/libc++.so" "${wasi_libraries}/noeh/libc++abi.so" DESTINATION .)
endif()
