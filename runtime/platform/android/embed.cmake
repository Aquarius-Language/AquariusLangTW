# Android link rules of native library outputs (runtime/embed/CMakeLists.txt includes this as platform.cmake; it sets
# parts, embed_archives, embed_symbols, embed_system and embed_cxx). One library per ABI, loadable by any app or process
# (no NativeActivity), aligned to 16 KB pages as Android 15 devices with 16 KB pages require.
#   shared   a version script exports exactly embed_symbols (JNI_OnLoad too with the Java binding), -Bsymbolic binds the
#            library's own references to itself; the C++ runtime links statically.
#   static   one relocatable object prelinked with ld.lld -r, every symbol but embed_symbols made local, in an archive.
list(APPEND embed_system log dl m)
set(embed_page_size -Wl,-z,max-page-size=16384 -Wl,-z,common-page-size=16384)

if(AQUA_EMBED_SHARED_OUTPUT)
    list(JOIN embed_symbols ";\n    " embed_exports)
    set(embed_version_script "${CMAKE_CURRENT_BINARY_DIR}/library.map")
    file(WRITE "${embed_version_script}" "{\n  global:\n    ${embed_exports};\n  local:\n    *;\n};\n")
    add_library(library SHARED $<TARGET_OBJECTS:parts>)
    target_link_libraries(library PRIVATE ${embed_archives} ${embed_system})
    target_link_options(library PRIVATE "-Wl,--version-script=${embed_version_script}" -Wl,-Bsymbolic -Wl,--no-undefined
        -Wl,-z,noexecstack ${embed_page_size} -static-libstdc++)
    get_filename_component(directory "${AQUA_EMBED_SHARED_OUTPUT}" DIRECTORY)
    get_filename_component(name "${AQUA_EMBED_SHARED_OUTPUT}" NAME)
    set_target_properties(library PROPERTIES PREFIX "" SUFFIX "" OUTPUT_NAME "${name}" LIBRARY_OUTPUT_DIRECTORY "${directory}")
    if(embed_cxx)
        set_target_properties(library PROPERTIES LINKER_LANGUAGE CXX)
    else()
        set_target_properties(library PROPERTIES LINKER_LANGUAGE C)
    endif()
endif()

if(AQUA_EMBED_OBJECT_OUTPUT)
    message(FATAL_ERROR "Android has no object kind of native library outputs.")
endif()
if(AQUA_EMBED_STATIC_OUTPUT)
    set(prelinked "${CMAKE_CURRENT_BINARY_DIR}/prelinked.o")
    add_custom_command(OUTPUT "${AQUA_EMBED_STATIC_OUTPUT}"
        COMMAND "${AQUA_LLVM}/bin/ld.lld${AQUA_EXE}" -r -o "${prelinked}" $<TARGET_OBJECTS:parts> ${embed_archives}
        COMMAND "${AQUA_LLVM}/bin/llvm-objcopy${AQUA_EXE}" "--keep-global-symbols=${AQUA_EMBED_TABLE}/symbols.txt" "${prelinked}"
        COMMAND "${CMAKE_COMMAND}" -E rm -f "${AQUA_EMBED_STATIC_OUTPUT}"
        COMMAND "${CMAKE_AR}" rcs "${AQUA_EMBED_STATIC_OUTPUT}" "${prelinked}"
        DEPENDS parts $<TARGET_OBJECTS:parts> ${embed_archives} COMMAND_EXPAND_LISTS VERBATIM)
    add_custom_target(static_output ALL DEPENDS "${AQUA_EMBED_STATIC_OUTPUT}")
endif()
