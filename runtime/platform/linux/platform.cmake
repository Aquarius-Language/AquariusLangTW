# The Linux runtime (runtime/CMakeLists.txt includes it for AQUA_OS linux): the desktop launchers with this layer and the
# POSIX one.
set(platform_sources platform/linux/platform.c platform/posix/posix.c platform/posix/desktop.c)
set(platform_entry platform/posix/entry.c)
set(platform_libraries pthread dl m)
include("${CMAKE_CURRENT_LIST_DIR}/../desktop/desktop.cmake")

if(GLFW_BUILD_X11)
    target_compile_definitions(aqua_window PRIVATE AQUA_GLFW_X11)
endif()
if(GLFW_BUILD_WAYLAND)
    target_compile_definitions(aqua_window PRIVATE AQUA_GLFW_WAYLAND)
endif()
target_link_options(aquarius-aot PRIVATE -Wl,-z,stack-size=67108864)
