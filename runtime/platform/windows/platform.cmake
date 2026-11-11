# The Windows runtime (runtime/CMakeLists.txt includes it for AQUA_OS windows): the desktop launchers with this layer.
enable_language(RC) # the executable's manifest
set(platform_sources platform/windows/platform.c)
set(platform_entry platform/windows/entry.c platform/windows/aquarius.rc)
set(platform_libraries ws2_32 userenv ntdll bcrypt ole32 shell32 advapi32 kernel32 synchronization)
include("${CMAKE_CURRENT_LIST_DIR}/../desktop/desktop.cmake")

# GLFW's window procedure file is compiled through a wrapper (platform/desktop/glfw_win32_window.c) that keeps the
# upstream source unchanged.
get_target_property(glfw_sources glfw SOURCES)
list(FILTER glfw_sources EXCLUDE REGEX "(^|/)win32_window\.c$")
list(APPEND glfw_sources "${CMAKE_CURRENT_SOURCE_DIR}/platform/desktop/glfw_win32_window.c")
set_property(TARGET glfw PROPERTY SOURCES "${glfw_sources}")
target_link_libraries(aqua_window PUBLIC imm32 ws2_32)
target_link_options(aquarius-aot PRIVATE -Wl,--stack,67108864)
