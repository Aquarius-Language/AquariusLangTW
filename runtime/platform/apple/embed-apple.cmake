# Apple's link rules of native library outputs (macOS and iOS), included by runtime/platform/macos/embed.cmake and
# runtime/platform/ios/embed.cmake, which runtime/embed/CMakeLists.txt includes as platform.cmake; it sets parts,
# embed_archives, embed_symbols, embed_system and embed_cxx. Apple's ld links, through the pinned Clang.
#   shared   a dylib exporting exactly AQUA_EMBED_TABLE/exported_symbols.txt (Mach-O names), with the install name
#            AQUA_EMBED_INSTALL_NAME (@rpath/lib<name>.dylib, or the framework's binary), AQUA_EMBED_CURRENT_VERSION and
#            AQUA_EMBED_COMPATIBILITY_VERSION, no undefined symbols, dead code stripped, and signed ad hoc (apps re-sign
#            what they embed). The system's libc++ is a dylib every Apple system ships.
#   static, object   one relocatable object prelinked with ld -r from the parts and what they use of the archives: the
#            exported symbols list makes every other symbol hidden, and ld -r turns hidden symbols local, so two
#            libraries never see each other's internals. The static output is that object in an archive (libtool).
if(embed_cxx)
    list(APPEND embed_system c++)
endif()
set(embed_exported "${AQUA_EMBED_TABLE}/exported_symbols.txt")
set(embed_link_target --target=${CMAKE_C_COMPILER_TARGET} -isysroot "${CMAKE_OSX_SYSROOT}")

if(AQUA_EMBED_SHARED_OUTPUT)
    add_library(library SHARED $<TARGET_OBJECTS:parts>)
    target_link_libraries(library PRIVATE ${embed_archives} ${embed_system})
    target_link_options(library PRIVATE "-Wl,-exported_symbols_list,${embed_exported}" -Wl,-undefined,error -Wl,-dead_strip
        "-Wl,-install_name,${AQUA_EMBED_INSTALL_NAME}" "-Wl,-current_version,${AQUA_EMBED_CURRENT_VERSION}"
        "-Wl,-compatibility_version,${AQUA_EMBED_COMPATIBILITY_VERSION}")
    get_filename_component(directory "${AQUA_EMBED_SHARED_OUTPUT}" DIRECTORY)
    get_filename_component(name "${AQUA_EMBED_SHARED_OUTPUT}" NAME)
    # The install name is the one above, not CMake's.
    set_target_properties(library PROPERTIES PREFIX "" SUFFIX "" OUTPUT_NAME "${name}" LIBRARY_OUTPUT_DIRECTORY "${directory}"
        NO_SONAME ON)
    if(embed_cxx)
        set_target_properties(library PROPERTIES LINKER_LANGUAGE CXX)
    else()
        set_target_properties(library PROPERTIES LINKER_LANGUAGE C)
    endif()
    add_custom_command(TARGET library POST_BUILD COMMAND codesign --force --sign - --timestamp=none "${AQUA_EMBED_SHARED_OUTPUT}" VERBATIM)
endif()

if(AQUA_EMBED_STATIC_OUTPUT OR AQUA_EMBED_OBJECT_OUTPUT)
    set(prelinked "${CMAKE_CURRENT_BINARY_DIR}/prelinked.o")
    add_custom_command(OUTPUT "${prelinked}"
        COMMAND "${CMAKE_C_COMPILER}" ${embed_link_target} -r -nostdlib "-Wl,-exported_symbols_list,${embed_exported}"
            -o "${prelinked}" $<TARGET_OBJECTS:parts> ${embed_archives}
        DEPENDS parts $<TARGET_OBJECTS:parts> ${embed_archives} "${embed_exported}" COMMAND_EXPAND_LISTS VERBATIM)
    set(outputs "")
    if(AQUA_EMBED_OBJECT_OUTPUT)
        add_custom_command(OUTPUT "${AQUA_EMBED_OBJECT_OUTPUT}" COMMAND "${CMAKE_COMMAND}" -E copy "${prelinked}" "${AQUA_EMBED_OBJECT_OUTPUT}"
            DEPENDS "${prelinked}" VERBATIM)
        list(APPEND outputs "${AQUA_EMBED_OBJECT_OUTPUT}")
    endif()
    if(AQUA_EMBED_STATIC_OUTPUT)
        add_custom_command(OUTPUT "${AQUA_EMBED_STATIC_OUTPUT}"
            COMMAND "${CMAKE_COMMAND}" -E rm -f "${AQUA_EMBED_STATIC_OUTPUT}"
            COMMAND xcrun libtool -static -no_warning_for_no_symbols -o "${AQUA_EMBED_STATIC_OUTPUT}" "${prelinked}"
            DEPENDS "${prelinked}" VERBATIM)
        list(APPEND outputs "${AQUA_EMBED_STATIC_OUTPUT}")
    endif()
    add_custom_target(prelinked_outputs ALL DEPENDS ${outputs})
endif()
