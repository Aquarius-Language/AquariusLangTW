# The web runtime (runtime/CMakeLists.txt includes it for AQUA_OS web).
# The host core as a side module; the glue links it after libc and before the libraries.
# With the embed runtime of native library outputs (embed.cmake): web-glue/core/library.mjs loads a library through it.
include("${CMAKE_CURRENT_LIST_DIR}/embed.cmake")
add_library(aquarius SHARED ${host_sources} host/engine_web.c host/threads_web.c platform/web/platform.c platform/web/window.c
    ${embed_web_sources})
target_include_directories(aquarius PRIVATE sdk/include "${CMAKE_CURRENT_BINARY_DIR}/generated")
set_target_properties(aquarius PROPERTIES OUTPUT_NAME aquarius)
get_property(web_libraries GLOBAL PROPERTY AQUA_WEB_LIBRARIES)
install(TARGETS aquarius LIBRARY DESTINATION . RUNTIME DESTINATION .)
if(web_libraries)
    install(TARGETS ${web_libraries} LIBRARY DESTINATION libraries RUNTIME DESTINATION libraries)
endif()
# The sysroot's shared C and C++ libraries the side modules link against.
set(wasi_libraries "${AQUA_WASI_SYSROOT}/lib/wasm32-wasip1")
install(FILES "${wasi_libraries}/libc.so" "${wasi_libraries}/noeh/libc++.so" "${wasi_libraries}/noeh/libc++abi.so" DESTINATION .)
