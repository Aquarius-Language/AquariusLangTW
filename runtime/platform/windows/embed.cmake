# Windows link rules of native library outputs (runtime/embed/CMakeLists.txt includes this as platform.cmake): the DLL
# exports exactly embed_symbols through a module definition file, links the C runtime pieces statically (only system
# DLLs remain) and comes with the import libraries asked for: AQUA_EMBED_IMPORT_MINGW (lib<name>.dll.a, the linker's)
# and AQUA_EMBED_IMPORT_MSVC (<name>.lib, from llvm-dlltool).
get_filename_component(embed_dll "${AQUA_AOT_OUTPUT}" NAME)
list(JOIN embed_symbols "\n    " embed_exports)
set(embed_definition "${CMAKE_CURRENT_BINARY_DIR}/library.def")
file(WRITE "${embed_definition}" "LIBRARY ${embed_dll}\nEXPORTS\n    ${embed_exports}\n")
target_link_options(library PRIVATE -static "${embed_definition}")
target_link_libraries(library PRIVATE ws2_32 userenv ntdll bcrypt ole32 shell32 advapi32 kernel32 synchronization)
if(AQUA_EMBED_IMPORT_MINGW)
    add_custom_command(TARGET library POST_BUILD
        COMMAND "${CMAKE_COMMAND}" -E copy "$<TARGET_LINKER_FILE:library>" "${AQUA_EMBED_IMPORT_MINGW}" VERBATIM)
endif()
if(AQUA_EMBED_IMPORT_MSVC)
    add_custom_command(TARGET library POST_BUILD
        COMMAND "${AQUA_LLVM}/bin/llvm-dlltool${AQUA_EXE}" -m i386:x86-64 -d "${embed_definition}" -l "${AQUA_EMBED_IMPORT_MSVC}" VERBATIM)
endif()
