# macOS on Apple Silicon: the pinned Clang against the SDK of the installed Xcode or Command Line Tools (xcrun
# --show-sdk-path), linked by Apple's ld, which signs every arm64 binary ad hoc as macOS requires and implements the
# options ld64.lld still lacks (-stack_size). The C++ runtime is the system's libc++, which every macOS ships, so
# binaries need only the system libraries and frameworks.
set(CMAKE_SYSTEM_NAME Darwin)
set(CMAKE_SYSTEM_PROCESSOR arm64)
set(CMAKE_OSX_ARCHITECTURES arm64 CACHE STRING "")
set(CMAKE_OSX_DEPLOYMENT_TARGET 13.0 CACHE STRING "")
set(AQUA_TARGET macos-arm64)
include("${CMAKE_CURRENT_LIST_DIR}/AquariusToolchain.cmake")
if(NOT CMAKE_OSX_SYSROOT)
    execute_process(COMMAND xcrun --sdk macosx --show-sdk-path OUTPUT_VARIABLE aqua_macos_sdk OUTPUT_STRIP_TRAILING_WHITESPACE
        RESULT_VARIABLE aqua_macos_sdk_result)
    if(NOT aqua_macos_sdk_result EQUAL 0 OR NOT aqua_macos_sdk)
        message(FATAL_ERROR "No macOS SDK found: install Xcode or the Command Line Tools (xcode-select --install).")
    endif()
    set(CMAKE_OSX_SYSROOT "${aqua_macos_sdk}" CACHE PATH "")
endif()
# C++ compiles against the headers of the libc++ it links, the SDK's (the system's), not the pinned LLVM's newer ones,
# whose inline code calls functions the libc++ of older systems and SDKs lacks.
foreach(language IN ITEMS CXX OBJCXX)
    string(APPEND CMAKE_${language}_FLAGS_INIT " -nostdinc++ -isystem ${CMAKE_OSX_SYSROOT}/usr/include/c++/v1")
endforeach()
set(CMAKE_C_COMPILER "${AQUA_LLVM}/bin/clang")
set(CMAKE_CXX_COMPILER "${AQUA_LLVM}/bin/clang++")
set(CMAKE_OBJC_COMPILER "${AQUA_LLVM}/bin/clang")
set(CMAKE_AR "${AQUA_LLVM}/bin/llvm-ar" CACHE FILEPATH "")
set(CMAKE_RANLIB "${AQUA_LLVM}/bin/llvm-ranlib" CACHE FILEPATH "")
set(CMAKE_C_COMPILER_TARGET arm64-apple-macos${CMAKE_OSX_DEPLOYMENT_TARGET})
set(CMAKE_CXX_COMPILER_TARGET arm64-apple-macos${CMAKE_OSX_DEPLOYMENT_TARGET})
set(CMAKE_OBJC_COMPILER_TARGET arm64-apple-macos${CMAKE_OSX_DEPLOYMENT_TARGET})
