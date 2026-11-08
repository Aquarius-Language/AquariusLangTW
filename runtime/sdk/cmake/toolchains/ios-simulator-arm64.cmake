# The iOS simulator on Apple Silicon Macs: the same platform as ios-arm64, a different SDK and binary format.
set(AQUA_TARGET ios-simulator-arm64)
set(AQUA_IOS_SDK iphonesimulator)
set(AQUA_IOS_SUFFIX "-simulator")
include("${CMAKE_CURRENT_LIST_DIR}/ios.cmake")
