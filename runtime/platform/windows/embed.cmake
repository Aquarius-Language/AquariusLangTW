# Windows link rules of native library outputs (runtime/embed/CMakeLists.txt includes this as platform.cmake; it sets
# parts, embed_archives, embed_symbols, embed_system and embed_cxx).
#   shared   the DLL exports exactly embed_symbols through a module definition file, links the C runtime pieces
#            statically (only system DLLs remain) and comes with the import libraries asked for:
#            AQUA_EMBED_IMPORT_MINGW (lib<name>.dll.a, the linker's) and AQUA_EMBED_IMPORT_MSVC (<name>.lib, llvm-dlltool).
#   static   lld cannot prelink COFF objects (no -r), so the archive holds the parts and every member of the archives
#            they link, each defined symbol but embed_symbols renamed with the library's prefix (embed-archive.cmake):
#            two libraries in one program never share an internal name. No object kind: it would need that prelink.
list(APPEND embed_system ws2_32 userenv ntdll bcrypt ole32 shell32 advapi32 kernel32 synchronization)
if(embed_cxx)
    list(APPEND embed_system c++ unwind)
endif()

if(AQUA_EMBED_SHARED_OUTPUT)
    get_filename_component(embed_dll "${AQUA_EMBED_SHARED_OUTPUT}" NAME)
    get_filename_component(directory "${AQUA_EMBED_SHARED_OUTPUT}" DIRECTORY)
    list(JOIN embed_symbols "\n    " embed_exports)
    set(embed_definition "${CMAKE_CURRENT_BINARY_DIR}/library.def")
    file(WRITE "${embed_definition}" "LIBRARY ${embed_dll}\nEXPORTS\n    ${embed_exports}\n")
    add_library(library SHARED $<TARGET_OBJECTS:parts>)
    target_link_options(library PRIVATE -static "${embed_definition}")
    # -Bstatic: the toolchain's shared-library flags end in -Bdynamic, which would import winpthread and libc++ as DLLs.
    target_link_libraries(library PRIVATE -Wl,-Bstatic ${embed_archives} ${embed_system})
    set_target_properties(library PROPERTIES PREFIX "" SUFFIX "" OUTPUT_NAME "${embed_dll}" RUNTIME_OUTPUT_DIRECTORY "${directory}"
        ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/archive")
    if(embed_cxx)
        set_target_properties(library PROPERTIES LINKER_LANGUAGE CXX)
    else()
        set_target_properties(library PROPERTIES LINKER_LANGUAGE C)
    endif()
    if(AQUA_EMBED_IMPORT_MINGW)
        add_custom_command(TARGET library POST_BUILD
            COMMAND "${CMAKE_COMMAND}" -E copy "$<TARGET_LINKER_FILE:library>" "${AQUA_EMBED_IMPORT_MINGW}" VERBATIM)
    endif()
    if(AQUA_EMBED_IMPORT_MSVC)
        add_custom_command(TARGET library POST_BUILD
            COMMAND "${AQUA_LLVM}/bin/llvm-dlltool${AQUA_EXE}" -m i386:x86-64 -d "${embed_definition}" -l "${AQUA_EMBED_IMPORT_MSVC}" VERBATIM)
    endif()
endif()

if(AQUA_EMBED_OBJECT_OUTPUT)
    message(FATAL_ERROR "Windows has no object kind of native library outputs (lld cannot prelink COFF objects).")
endif()
if(AQUA_EMBED_STATIC_OUTPUT)
    set(objects_list "${CMAKE_CURRENT_BINARY_DIR}/objects.txt")
    file(GENERATE OUTPUT "${objects_list}" CONTENT "$<JOIN:$<TARGET_OBJECTS:parts>,\n>\n")
    string(REPLACE ";" "|" archives "${embed_archives}")
    add_custom_command(OUTPUT "${AQUA_EMBED_STATIC_OUTPUT}"
        COMMAND "${CMAKE_COMMAND}" "-DOBJECTS=${objects_list}" "-DARCHIVES=${archives}" "-DSYMBOLS=${AQUA_EMBED_TABLE}/symbols.txt"
            "-DPREFIX=${AQUA_EMBED_PREFIX}" "-DOUTPUT=${AQUA_EMBED_STATIC_OUTPUT}" "-DLLVM=${AQUA_LLVM}/bin" "-DEXE=${AQUA_EXE}"
            "-DWORK=${CMAKE_CURRENT_BINARY_DIR}/static" -P "${CMAKE_CURRENT_SOURCE_DIR}/embed-archive.cmake"
        DEPENDS parts $<TARGET_OBJECTS:parts> ${embed_archives} "${objects_list}" VERBATIM)
    add_custom_target(static_output ALL DEPENDS "${AQUA_EMBED_STATIC_OUTPUT}")
endif()
