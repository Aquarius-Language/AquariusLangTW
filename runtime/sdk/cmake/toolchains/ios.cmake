# iOS and iPadOS: the pinned Clang against the iPhoneOS or iPhoneSimulator SDK of the installed Xcode (xcrun
# --show-sdk-path), linked by Apple's ld, for the target the including file sets (ios-arm64.cmake: devices,
# ios-simulator-arm64.cmake: the simulator on Apple Silicon Macs). Everything is static: iOS applications carry no loose
# shared libraries, and with no run-time code generation the application runs ahead-of-time compiled (wasm2c).
set(CMAKE_SYSTEM_NAME iOS)
set(CMAKE_SYSTEM_PROCESSOR arm64)
set(CMAKE_OSX_ARCHITECTURES arm64 CACHE STRING "")
set(CMAKE_OSX_DEPLOYMENT_TARGET 16.0 CACHE STRING "")
include("${CMAKE_CURRENT_LIST_DIR}/AquariusToolchain.cmake")
if(NOT CMAKE_OSX_SYSROOT)
    execute_process(COMMAND xcrun --sdk ${AQUA_IOS_SDK} --show-sdk-path OUTPUT_VARIABLE aqua_ios_sdk OUTPUT_STRIP_TRAILING_WHITESPACE
        RESULT_VARIABLE aqua_ios_sdk_result)
    if(NOT aqua_ios_sdk_result EQUAL 0 OR NOT aqua_ios_sdk)
        message(FATAL_ERROR "No ${AQUA_IOS_SDK} SDK found: install Xcode with the iOS platform and run xcode-select -s /Applications/Xcode.app.")
    endif()
    set(CMAKE_OSX_SYSROOT "${aqua_ios_sdk}" CACHE PATH "")
endif()
set(CMAKE_C_COMPILER "${AQUA_LLVM}/bin/clang")
set(CMAKE_CXX_COMPILER "${AQUA_LLVM}/bin/clang++")
set(CMAKE_OBJC_COMPILER "${AQUA_LLVM}/bin/clang")
set(CMAKE_AR "${AQUA_LLVM}/bin/llvm-ar" CACHE FILEPATH "")
set(CMAKE_RANLIB "${AQUA_LLVM}/bin/llvm-ranlib" CACHE FILEPATH "")
foreach(language IN ITEMS C CXX OBJC)
    set(CMAKE_${language}_COMPILER_TARGET arm64-apple-ios${CMAKE_OSX_DEPLOYMENT_TARGET}${AQUA_IOS_SUFFIX})
endforeach()
# CMake's compiler checks link an executable, which iOS would need signed: check with a static library instead.
set(CMAKE_TRY_COMPILE_TARGET_TYPE STATIC_LIBRARY)
