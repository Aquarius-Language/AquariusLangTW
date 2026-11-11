# Linux link rules of native library outputs (runtime/embed/CMakeLists.txt includes this as platform.cmake; it sets
# parts, embed_archives, embed_symbols, embed_system and embed_cxx).
#   shared   a version script exports exactly embed_symbols, -Bsymbolic binds the library's own references to itself,
#            the SONAME is AQUA_EMBED_SONAME (lib<name>.so.<major>); only the C library's parts remain as dependencies.
#   static, object   one relocatable object prelinked with ld.lld -r from the parts and what they use of the archives
#            (the C++ runtime too, for C++ libraries), every symbol but embed_symbols made local: two libraries never
#            see each other's internals. The static output is that object in an archive.
list(APPEND embed_system pthread dl m)
set(embed_cxx_runtime "")
if(embed_cxx)
    foreach(archive IN ITEMS libc++.a libc++abi.a)
        execute_process(COMMAND "${CMAKE_CXX_COMPILER}" --target=${CMAKE_CXX_COMPILER_TARGET} -print-file-name=${archive}
            OUTPUT_VARIABLE path OUTPUT_STRIP_TRAILING_WHITESPACE)
        list(APPEND embed_cxx_runtime "${path}")
    endforeach()
endif()

if(AQUA_EMBED_SHARED_OUTPUT)
    list(JOIN embed_symbols ";\n    " embed_exports)
    set(embed_version_script "${CMAKE_CURRENT_BINARY_DIR}/library.map")
    file(WRITE "${embed_version_script}" "{\n  global:\n    ${embed_exports};\n  local:\n    *;\n};\n")
    add_library(library SHARED $<TARGET_OBJECTS:parts>)
    target_link_libraries(library PRIVATE ${embed_archives} ${embed_system})
    target_link_options(library PRIVATE "-Wl,--version-script=${embed_version_script}" -Wl,-Bsymbolic -Wl,--no-undefined
        -Wl,-z,noexecstack "-Wl,-soname,${AQUA_EMBED_SONAME}")
    get_filename_component(directory "${AQUA_EMBED_SHARED_OUTPUT}" DIRECTORY)
    get_filename_component(name "${AQUA_EMBED_SHARED_OUTPUT}" NAME)
    set_target_properties(library PROPERTIES PREFIX "" SUFFIX ""
        OUTPUT_NAME "${name}" LIBRARY_OUTPUT_DIRECTORY "${directory}")
    if(embed_cxx)
        set_target_properties(library PROPERTIES LINKER_LANGUAGE CXX)
    else()
        set_target_properties(library PROPERTIES LINKER_LANGUAGE C)
    endif()
endif()

if(AQUA_EMBED_STATIC_OUTPUT OR AQUA_EMBED_OBJECT_OUTPUT)
    set(prelinked "${CMAKE_CURRENT_BINARY_DIR}/prelinked.o")
    add_custom_command(OUTPUT "${prelinked}"
        COMMAND "${AQUA_LLVM}/bin/ld.lld" -r -o "${prelinked}" $<TARGET_OBJECTS:parts> ${embed_archives} ${embed_cxx_runtime}
        COMMAND "${AQUA_LLVM}/bin/llvm-objcopy" "--keep-global-symbols=${AQUA_EMBED_TABLE}/symbols.txt" "${prelinked}"
        DEPENDS parts $<TARGET_OBJECTS:parts> ${embed_archives} COMMAND_EXPAND_LISTS VERBATIM)
    set(outputs "")
    if(AQUA_EMBED_OBJECT_OUTPUT)
        add_custom_command(OUTPUT "${AQUA_EMBED_OBJECT_OUTPUT}" COMMAND "${CMAKE_COMMAND}" -E copy "${prelinked}" "${AQUA_EMBED_OBJECT_OUTPUT}"
            DEPENDS "${prelinked}" VERBATIM)
        list(APPEND outputs "${AQUA_EMBED_OBJECT_OUTPUT}")
    endif()
    if(AQUA_EMBED_STATIC_OUTPUT)
        add_custom_command(OUTPUT "${AQUA_EMBED_STATIC_OUTPUT}"
            COMMAND "${CMAKE_COMMAND}" -E rm -f "${AQUA_EMBED_STATIC_OUTPUT}"
            COMMAND "${CMAKE_AR}" rcs "${AQUA_EMBED_STATIC_OUTPUT}" "${prelinked}" DEPENDS "${prelinked}" VERBATIM)
        list(APPEND outputs "${AQUA_EMBED_STATIC_OUTPUT}")
    endif()
    add_custom_target(prelinked_outputs ALL DEPENDS ${outputs})
endif()
