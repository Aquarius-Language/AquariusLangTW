# macOS link rules of native library outputs (runtime/embed/CMakeLists.txt includes this as platform.cmake): Apple's,
# shared with iOS (runtime/platform/apple/embed-apple.cmake, installed beside this file).
include("${CMAKE_CURRENT_LIST_DIR}/embed-apple.cmake")
