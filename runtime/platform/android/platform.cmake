# The Android runtime (runtime/CMakeLists.txt includes it for AQUA_OS android).
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
