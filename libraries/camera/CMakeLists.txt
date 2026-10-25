# 攝影機/Webcam (Windows: Media Foundation, Linux: V4L2, web: getUserMedia through web-glue/camera) and Camera/相機
# (Android: NDK Camera2). Builds inside the runtime (a default library) or on its own as an external library:
#   cmake -S . -B build -G Ninja -DCMAKE_TOOLCHAIN_FILE=<runtime/sdk/cmake/toolchains/<target>.cmake>
cmake_minimum_required(VERSION 3.20)
project(aqua_camera C)
include(AquariusLibrary)
aqua_add_library(camera SOURCES src/camera.c src/convert.c
    src/platform/windows.c src/platform/linux.c src/platform/android.c src/platform/web.c src/platform/unsupported.c)
if(AQUA_TARGET STREQUAL "windows-x64")
    # winpthreads linked statically, so the external aqua_camera.dll needs no libwinpthread-1.dll beside it.
    target_link_libraries(aqua_camera PRIVATE mfplat mf mfreadwrite mfuuid ole32 -l:libwinpthread.a)
elseif(ANDROID)
    target_link_libraries(aqua_camera PRIVATE camera2ndk mediandk m)
elseif(NOT AQUA_TARGET STREQUAL "web")
    find_package(Threads REQUIRED)
    target_link_libraries(aqua_camera PRIVATE Threads::Threads m)
endif()
