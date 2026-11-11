# The macOS runtime (runtime/CMakeLists.txt includes it for AQUA_OS macos): the desktop launchers with this layer and the
# POSIX one. GLFW uses Cocoa, IOKit and QuartzCore; Metal is for libraries that draw through a CAMetalLayer
# (platform/desktop/metal_layer.m makes one for a GLFW window).
enable_language(OBJC)
set(platform_sources platform/macos/platform.c platform/posix/posix.c platform/posix/desktop.c platform/desktop/metal_layer.m)
set(platform_entry platform/posix/entry.c)
set(platform_libraries pthread m "-framework Cocoa" "-framework IOKit" "-framework QuartzCore" "-framework Metal"
    "-framework CoreFoundation" "-framework Security")
include("${CMAKE_CURRENT_LIST_DIR}/../desktop/desktop.cmake")

target_link_options(aquarius-aot PRIVATE -Wl,-stack_size,0x4000000)

# Native library outputs (runtime/embed) link the layer without the Metal layer of GLFW windows, which they cannot open,
# with Apple's link rules (embed.cmake includes platform/apple/embed-apple.cmake).
list(REMOVE_ITEM embed_platform_sources platform/desktop/metal_layer.m)
list(APPEND embed_platform_scripts "${CMAKE_CURRENT_LIST_DIR}/../apple/embed-apple.cmake")
