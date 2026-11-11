# The embed runtime of native library outputs (docs/native-libraries.md), included by runtime/CMakeLists.txt for a
# platform whose layer has an embed.cmake. It builds the host core with the ahead-of-time engine linked in (AQUA_EMBED),
# the layer's sources (embed_platform_sources: its platform.cmake or the shared desktop one sets them) and no window
# system into embed/libaquarius_embed.a, position independent with hidden symbols, and installs beside it what aqua
# build compiles each library with: CMakeLists.txt (the library project), exports.c, aqua/embed.h, the ahead-of-time
# glue and the layer's link rules as platform.cmake. Default libraries are not part of it yet.
add_library(aquarius_embed STATIC ${host_sources} host/engine_aot.c host/sha256.c host/threads.c ${embed_platform_sources}
    embed/embed.c embed/no_window.c)
target_include_directories(aquarius_embed PRIVATE sdk/include)
target_compile_definitions(aquarius_embed PRIVATE AQUA_EMBED)
set_target_properties(aquarius_embed PROPERTIES POSITION_INDEPENDENT_CODE ON C_VISIBILITY_PRESET hidden)
install(TARGETS aquarius_embed ARCHIVE DESTINATION embed)
install(FILES embed/CMakeLists.txt embed/exports.c aot/aot_app.c aot/aot_app.h host/guest_exports.h DESTINATION embed)
install(FILES embed/aqua/embed.h DESTINATION embed/aqua)
install(FILES "platform/${AQUA_OS}/embed.cmake" DESTINATION embed RENAME platform.cmake)
