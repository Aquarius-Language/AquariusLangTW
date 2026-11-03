# Builds WABT's wasm2c (aqua build --optimize) from its pinned source with the pinned toolchain, so every host gets the
# same self-contained tool: SHA-256 from WABT's bundled PicoSHA2 instead of OpenSSL's libcrypto (which WABT's prebuilt
# Windows wasm2c needs without shipping it, and its Linux one needs from the system), the C++ runtime linked
# statically. Lays out what AheadOfTime reads: bin/wasm2c(.exe), include/wasm-rt*.h, share/wabt/wasm2c/.
# Run by install-toolchain.ps1 / .sh:
#   cmake -DSOURCE=<wabt source> -DDESTINATION=<toolchain folder> -DTOOLCHAIN=<runtime/sdk/cmake/toolchains/<host>.cmake>
#         -DNINJA=<ninja> -P scripts/build-wabt.cmake
foreach(variable SOURCE DESTINATION TOOLCHAIN NINJA)
    if(NOT DEFINED ${variable})
        message(FATAL_ERROR "build-wabt.cmake needs -D${variable}=...")
    endif()
endforeach()
set(build "${DESTINATION}.build")
file(REMOVE_RECURSE "${build}")
# The source release carries the config.h generated on the machine that packaged it (Linux), which would shadow the
# one this configuration generates (Windows has no alloca.h, for one).
file(REMOVE "${SOURCE}/include/wabt/config.h")
execute_process(COMMAND "${CMAKE_COMMAND}" -S "${SOURCE}" -B "${build}" -G Ninja "-DCMAKE_MAKE_PROGRAM=${NINJA}"
        "-DCMAKE_TOOLCHAIN_FILE=${TOOLCHAIN}" -DCMAKE_BUILD_TYPE=Release -DUSE_INTERNAL_SHA256=ON -DBUILD_TESTS=OFF
        -DBUILD_LIBWASM=OFF -DWABT_INSTALL_RULES=OFF
    RESULT_VARIABLE configured)
if(NOT configured EQUAL 0)
    message(FATAL_ERROR "Configuring WABT failed.")
endif()
execute_process(COMMAND "${CMAKE_COMMAND}" --build "${build}" --target wasm2c RESULT_VARIABLE built)
if(NOT built EQUAL 0)
    message(FATAL_ERROR "Building wasm2c failed.")
endif()
file(GLOB program "${build}/wasm2c" "${build}/wasm2c.exe")
file(MAKE_DIRECTORY "${DESTINATION}/bin" "${DESTINATION}/include" "${DESTINATION}/share/wabt/wasm2c")
file(COPY ${program} DESTINATION "${DESTINATION}/bin")
file(COPY "${SOURCE}/wasm2c/wasm-rt.h" "${SOURCE}/wasm2c/wasm-rt-exceptions.h" DESTINATION "${DESTINATION}/include")
file(GLOB runtime "${SOURCE}/wasm2c/wasm-rt-*impl*.c" "${SOURCE}/wasm2c/wasm-rt-impl.h" "${SOURCE}/wasm2c/*.inc")
file(COPY ${runtime} DESTINATION "${DESTINATION}/share/wabt/wasm2c")
file(REMOVE_RECURSE "${build}")
