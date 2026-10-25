# ONNX Runtime for the web: its source release compiled with the web toolchain (wasm32-wasip1, position independent,
# no exceptions, no threads, no Emscripten) into static libraries that link into this library's side module. The
# small changes it needs are in ../patches (see patches/README.md). The build runs once per build directory at
# configure time and takes several minutes; AQUA_ONNX_WEB_ORT_BUILD reuses an existing one.
#
# In:  onnx_version, onnx_download (CMakeLists.txt). Out: onnx_web_include, onnx_web_libraries.
enable_language(CXX)
set(AQUA_ONNX_WEB_ORT_BUILD "" CACHE PATH "An ONNX Runtime web build directory made by this file, reused instead of building")

# The source release (GitHub's archive of the v${onnx_version} tag) and the dependencies its CMake fetches, pinned here
# by SHA-256 and handed to it as a local mirror (onnxruntime_CMAKE_DEPS_MIRROR_DIR); ONNX Runtime checks its own SHA-1
# pins (cmake/deps.txt) again.
onnx_download(https://github.com/microsoft/onnxruntime/archive/refs/tags/v${onnx_version}.tar.gz
    f6681ecbddf53898adf0cc9e8e9e84657485b84d2eca3c8aa353de6d7dd417ef onnxruntime-${onnx_version}-src.tar.gz source_archive)
set(dependencies
    "https://github.com/abseil/abseil-cpp/archive/refs/tags/20250814.0.zip|b2bdcf6682d8cb53df365bcc5d6c318a22e55821d9978a10fdb61404c026daff"
    "https://github.com/HowardHinnant/date/archive/refs/tags/v3.0.1.zip|f4300b96f7a304d4ef9bf6e0fa3ded72159f7f2d0f605bdde3e030a0dba7cf9f"
    "https://github.com/eigen-mirror/eigen/archive/1d8b82b0740839c0de7f1242a3585e3390ff5f33/eigen-1d8b82b0740839c0de7f1242a3585e3390ff5f33.zip|6a60d76351f97132669daeeb721d6bf14b008101883ad2d687a3201c5c461eb0"
    "https://github.com/google/flatbuffers/archive/refs/tags/v23.5.26.zip|57bd580c0772fd1a726c34ab8bf05325293bc5f9c165060a898afa1feeeb95e1"
    "https://github.com/microsoft/GSL/archive/refs/tags/v4.2.1.zip|c9291d95f5f6e5c561990d06a589b01d89e553d0f366f0ce723dd1788e7a6076"
    "https://github.com/boostorg/mp11/archive/refs/tags/boost-1.82.0.zip|81431bdc44c439a324e02c07ed067f8f556419fd86f2d8b486ff568df6aac899"
    "https://github.com/nlohmann/json/archive/refs/tags/v3.11.3.zip|04022b05d806eb5ff73023c280b68697d12b93e1b7267a0b22a1a39ec7578069"
    "https://github.com/onnx/onnx/archive/refs/tags/v1.22.0.zip|8dc1181d33529a1249e031226126d0699ac9bdfc571ee530ee3a12f4656f2be3"
    "https://github.com/protocolbuffers/protobuf/releases/download/v33.6/protobuf-33.6.tar.gz|16498d7dc7967e9b100632138babd4b86b61592beeccdd556f67539d9c231355"
    "https://github.com/google/re2/archive/refs/tags/2024-07-02.zip|a835fe55fbdcd8e80f38584ab22d0840662c67f2feb36bd679402da9641dc71e"
    "https://github.com/dcleblanc/SafeInt/archive/refs/tags/3.0.28.zip|3ffbd9a2fdff45da77da3e7269e9aa512ea43bed5c38ce8fd8f3d1068a032c3f")
# protoc runs on the build host to generate ONNX's protobuf sources.
if(CMAKE_HOST_WIN32)
    list(APPEND dependencies "https://github.com/protocolbuffers/protobuf/releases/download/v33.6/protoc-33.6-win64.zip|96a83b8ee942f1046f079c43657cc09fd41f37a4e349ddb0e8ba5064c48bb5ff")
elseif(CMAKE_HOST_SYSTEM_NAME STREQUAL "Linux" AND CMAKE_HOST_SYSTEM_PROCESSOR MATCHES "x86_64|AMD64")
    list(APPEND dependencies "https://github.com/protocolbuffers/protobuf/releases/download/v33.6/protoc-33.6-linux-x86_64.zip|9c49962391ff8b5754342509efc3885db91dfe11a358e1e98c9a71518f0ad199")
endif()
set(mirror "${AQUA_ONNX_DOWNLOADS}/onnxruntime-deps")
foreach(entry IN LISTS dependencies)
    string(REPLACE "|" ";" entry "${entry}")
    list(GET entry 0 url)
    list(GET entry 1 sha256)
    string(REGEX REPLACE "^https://" "" relative "${url}")
    onnx_download("${url}" ${sha256} "onnxruntime-deps/${relative}" unused)
endforeach()

# The source tree, extracted and patched once per set of patches.
set(source "${CMAKE_CURRENT_BINARY_DIR}/onnxruntime-src")
file(GLOB patches "${CMAKE_CURRENT_SOURCE_DIR}/patches/*.patch")
list(SORT patches)
set(patch_state "${source_archive}")
foreach(patch IN LISTS patches)
    file(SHA256 "${patch}" hash)
    string(APPEND patch_state "\n${patch} ${hash}")
endforeach()
set(stamp "${source}/.aquarius-patched")
set(applied "")
if(EXISTS "${stamp}")
    file(READ "${stamp}" applied)
endif()
if(NOT applied STREQUAL patch_state)
    message(STATUS "onnx: extracting and patching ONNX Runtime ${onnx_version}")
    file(REMOVE_RECURSE "${source}" "${CMAKE_CURRENT_BINARY_DIR}/onnxruntime-web")
    file(ARCHIVE_EXTRACT INPUT "${source_archive}" DESTINATION "${source}-unpack"
        PATTERNS "*/cmake/*" "*/include/*" "*/onnxruntime/core/*" "*/onnxruntime/contrib_ops/*" "*/onnxruntime/lora/*"
                 "*/model_package/*" "*/VERSION_NUMBER" "*/LICENSE" "*/ThirdPartyNotices.txt")
    file(GLOB top LIST_DIRECTORIES true "${source}-unpack/onnxruntime-*")
    file(RENAME "${top}" "${source}")
    file(REMOVE_RECURSE "${source}-unpack")
    find_package(Patch REQUIRED)
    foreach(patch IN LISTS patches)
        execute_process(COMMAND "${Patch_EXECUTABLE}" -p1 --binary --forward -i "${patch}" WORKING_DIRECTORY "${source}"
            RESULT_VARIABLE result OUTPUT_VARIABLE output ERROR_VARIABLE output)
        if(result)
            message(FATAL_ERROR "onnx: ${patch} does not apply to ONNX Runtime ${onnx_version}:\n${output}")
        endif()
    endforeach()
    file(WRITE "${stamp}" "${patch_state}")
endif()
set(onnx_web_include "${source}/include/onnxruntime/core/session")

# Configure and build the libraries the CPU execution provider needs.
if(AQUA_ONNX_WEB_ORT_BUILD)
    set(build "${AQUA_ONNX_WEB_ORT_BUILD}")
else()
    set(build "${CMAKE_CURRENT_BINARY_DIR}/onnxruntime-web")
    set(targets onnxruntime_session onnxruntime_providers onnxruntime_optimizer onnxruntime_framework onnxruntime_graph
        onnxruntime_util onnxruntime_mlas onnxruntime_common onnxruntime_flatbuffers onnxruntime_lora re2)
    if(NOT EXISTS "${build}/.aquarius-built")
        message(STATUS "onnx: building ONNX Runtime ${onnx_version} for the web (once per build directory; several minutes)")
        # MLAS wasm SIMD kernels; every symbol stays private to the side module (the toolchain defaults to visible ones,
        # which the presets below override); WASI emulation headers declare what the posix layer refers to.
        set(flags "-msimd128 -D_WASI_EMULATED_MMAN -D_WASI_EMULATED_SIGNAL -D_WASI_EMULATED_PROCESS_CLOCKS -D_WASI_EMULATED_GETPID")
        set(tls "")
        if(CMAKE_TLS_CAINFO)
            set(tls "-DCMAKE_TLS_CAINFO=${CMAKE_TLS_CAINFO}")
        endif()
        execute_process(COMMAND "${CMAKE_COMMAND}" -E env "CFLAGS=${flags}" "CXXFLAGS=${flags} -Wno-deprecated-attributes"
            "${CMAKE_COMMAND}" --compile-no-warning-as-error -S "${source}/cmake" -B "${build}" -G "${CMAKE_GENERATOR}"
            "-DCMAKE_MAKE_PROGRAM=${CMAKE_MAKE_PROGRAM}" "-DCMAKE_TOOLCHAIN_FILE=${CMAKE_TOOLCHAIN_FILE}" -DCMAKE_BUILD_TYPE=Release
            ${tls} -DIconv_IS_BUILT_IN=TRUE "-Donnxruntime_CMAKE_DEPS_MIRROR_DIR=${mirror}"
            -Donnxruntime_BUILD_SHARED_LIB=OFF -Donnxruntime_BUILD_UNIT_TESTS=OFF -Donnxruntime_ENABLE_PYTHON=OFF
            -Donnxruntime_USE_TELEMETRY=OFF -Donnxruntime_ENABLE_CPUINFO=OFF -Donnxruntime_DISABLE_EXCEPTIONS=ON
            -Donnxruntime_RUN_ONNX_TESTS=OFF -Donnxruntime_BUILD_BENCHMARKS=OFF -Donnxruntime_USE_MIMALLOC=OFF
            -DCMAKE_C_VISIBILITY_PRESET=hidden -DCMAKE_CXX_VISIBILITY_PRESET=hidden -DCMAKE_VISIBILITY_INLINES_HIDDEN=ON
            RESULT_VARIABLE result OUTPUT_VARIABLE output ERROR_VARIABLE output)
        if(result)
            file(WRITE "${build}/aquarius-configure.log" "${output}")
            message(FATAL_ERROR "onnx: configuring ONNX Runtime for the web failed; see ${build}/aquarius-configure.log")
        endif()
        cmake_host_system_information(RESULT jobs QUERY NUMBER_OF_LOGICAL_CORES)
        execute_process(COMMAND "${CMAKE_COMMAND}" --build "${build}" --parallel ${jobs} --target ${targets}
            RESULT_VARIABLE result OUTPUT_VARIABLE output ERROR_VARIABLE output)
        if(result)
            file(WRITE "${build}/aquarius-build.log" "${output}")
            message(FATAL_ERROR "onnx: building ONNX Runtime for the web failed; see ${build}/aquarius-build.log")
        endif()
        file(WRITE "${build}/.aquarius-built" "${patch_state}")
    endif()
endif()
file(GLOB_RECURSE onnx_web_libraries "${build}/*.a")
list(FILTER onnx_web_libraries EXCLUDE REGEX "/CMakeFiles/")
