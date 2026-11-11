# Linux link rules of native library outputs (runtime/embed/CMakeLists.txt includes this as platform.cmake): a version
# script exports exactly embed_symbols, -Bsymbolic binds the library's own references to itself, and the SONAME is
# AQUA_EMBED_SONAME (lib<name>.so.<major>). Only the C library's parts remain as dependencies.
list(JOIN embed_symbols ";\n    " embed_exports)
set(embed_version_script "${CMAKE_CURRENT_BINARY_DIR}/library.map")
file(WRITE "${embed_version_script}" "{\n  global:\n    ${embed_exports};\n  local:\n    *;\n};\n")
target_link_options(library PRIVATE "-Wl,--version-script=${embed_version_script}" -Wl,-Bsymbolic -Wl,--no-undefined
    -Wl,-z,noexecstack "-Wl,-soname,${AQUA_EMBED_SONAME}")
target_link_libraries(library PRIVATE pthread dl m)
