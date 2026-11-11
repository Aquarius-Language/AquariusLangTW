# iOS link rules of native library outputs (runtime/embed/CMakeLists.txt includes this as platform.cmake): Apple's,
# shared with macOS (runtime/platform/apple/embed-apple.cmake, installed beside this file). aqua build wraps each
# slice's dylib into a framework and gathers the slices into an xcframework (AppleLibraryPackage).
include("${CMAKE_CURRENT_LIST_DIR}/embed-apple.cmake")
