# The onnx library: ONNX Runtime inference (module ONNX|推論引擎|ONNXRuntime) and the MediaPipe pose tracker
# (module MediaPipe|姿態追蹤). Builds inside the runtime (a default library) or on its own as an external library:
#   cmake -S . -B build -G Ninja -DCMAKE_TOOLCHAIN_FILE=<runtime/sdk/cmake/toolchains/<target>.cmake>
#
# Native targets use the official prebuilt ONNX Runtime release for the target, downloaded below and pinned by
# SHA-256. The library loads it at first use (src/platform), so applications that never import ONNX run without it.
# Its runtime files are copied next to the built runtime (and installed beside it) for the exporter to ship. The web
# compiles ONNX Runtime's source release into the side module instead (cmake/onnxruntime-web.cmake, patches/).
cmake_minimum_required(VERSION 3.21)
project(aqua_onnx C)
include(AquariusLibrary)

set(onnx_version 1.30.0)
option(AQUA_ONNX_CUDA "Stage ONNX Runtime's CUDA 13 build (adds the CUDA and TensorRT providers, ~300 MB) on windows-x64 and linux-x64" OFF)
set(AQUA_ONNX_DOWNLOADS "${CMAKE_CURRENT_BINARY_DIR}/downloads" CACHE PATH
    "Directory for the pinned ONNX Runtime packages (point several build directories at one to download them once)")

# Downloads `file` from `url` into AQUA_ONNX_DOWNLOADS unless a copy with the pinned hash is already there.
function(onnx_download url sha256 file out)
    set(path "${AQUA_ONNX_DOWNLOADS}/${file}")
    if(EXISTS "${path}")
        file(SHA256 "${path}" actual)
        if(NOT actual STREQUAL sha256)
            file(REMOVE "${path}")
        endif()
    endif()
    if(NOT EXISTS "${path}")
        message(STATUS "onnx: downloading ${url}")
        file(DOWNLOAD "${url}" "${path}.part" EXPECTED_HASH SHA256=${sha256} TLS_VERIFY ON STATUS status)
        list(GET status 0 code)
        if(code)
            file(REMOVE "${path}.part")
            message(FATAL_ERROR "onnx: download of ${url} failed: ${status}")
        endif()
        file(RENAME "${path}.part" "${path}")
    endif()
    set(${out} "${path}" PARENT_SCOPE)
endfunction()

# Extracts the members of `archive` matching the globs into `directory` once.
function(onnx_extract archive directory)
    if(NOT EXISTS "${directory}/.extracted")
        file(REMOVE_RECURSE "${directory}")
        file(ARCHIVE_EXTRACT INPUT "${archive}" DESTINATION "${directory}" PATTERNS ${ARGN})
        file(WRITE "${directory}/.extracted" "${archive}\n")
    endif()
endfunction()

set(sources src/library.c src/onnx.c src/policy.c src/tensor.c src/backend_ort.c src/pose.c src/mediapipe.c)
set(include_directories "")
set(link_libraries "")
set(runtime_files "")   # copied beside the runtime and installed next to it
set(license_files "")   # installed under licenses/

if(AQUA_TARGET STREQUAL "windows-x64" OR AQUA_TARGET STREQUAL "linux-x64")
    set(release https://github.com/microsoft/onnxruntime/releases/download/v${onnx_version})
    if(AQUA_TARGET STREQUAL "windows-x64")
        set(package onnxruntime-win-x64-${onnx_version}.zip)
        set(package_sha256 c6ba983baf5681af108599675d2a89c2d145512d02de28aed0bff177cd0ba949)
        if(AQUA_ONNX_CUDA)
            set(package onnxruntime-win-x64-gpu_cuda13-${onnx_version}.zip)
            set(package_sha256 8fa4b08359af682cd605892cb59077049700b640128bb93fd2c7776cf9f55bdc)
        endif()
        set(binaries "*/lib/*.dll")
        set(plugin_rid win-x64)
        list(APPEND sources src/platform/windows.c)
    else()
        set(package onnxruntime-linux-x64-${onnx_version}.tgz)
        set(package_sha256 a5ed5a3cac51fbb2e90da632ae43d19212faaa20e76484e62bcb7c23ddb3b3fd)
        if(AQUA_ONNX_CUDA)
            set(package onnxruntime-linux-x64-gpu_cuda13-${onnx_version}.tgz)
            set(package_sha256 382d79133112388cf94ce5855789b7c9bef12bef76a08b6b277e5a317213adcd)
        endif()
        set(binaries "*/lib/*.so*")
        set(plugin_rid linux-x64)
        list(APPEND sources src/platform/posix.c)
        list(APPEND link_libraries dl pthread)
    endif()
    onnx_download("${release}/${package}" ${package_sha256} ${package} archive)
    string(REGEX REPLACE "\\.(zip|tgz)$" "" unpacked "${package}")
    set(unpacked "${CMAKE_CURRENT_BINARY_DIR}/${unpacked}")
    onnx_extract("${archive}" "${unpacked}" "*/include/*" "${binaries}" "*/LICENSE" "*/ThirdPartyNotices.txt")
    file(GLOB root LIST_DIRECTORIES true "${unpacked}/onnxruntime-*")  # the archive's single top folder
    list(APPEND include_directories "${root}/include")
    file(GLOB engine_files "${root}/lib/*.dll" "${root}/lib/libonnxruntime*.so")
    if(AQUA_TARGET STREQUAL "linux-x64")
        # The release ships libonnxruntime.so -> .so.1 -> .so.1.30.0; the library loads it by its soname.
        list(REMOVE_ITEM engine_files "${root}/lib/libonnxruntime.so")
        file(COPY_FILE "${root}/lib/libonnxruntime.so.${onnx_version}" "${unpacked}/libonnxruntime.so.1")
        list(APPEND engine_files "${unpacked}/libonnxruntime.so.1")
    endif()
    list(APPEND runtime_files ${engine_files})
    list(APPEND license_files "onnxruntime|${root}/LICENSE" "onnxruntime|${root}/ThirdPartyNotices.txt")

    # The WebGPU plugin execution provider (Dawn on D3D12 or Vulkan) is published on nuget.org only.
    onnx_download(https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime.ep.webgpu/0.4.0/microsoft.ml.onnxruntime.ep.webgpu.0.4.0.nupkg
        cc2e093a2eaa760a630d6c3299f015f1dd86885b9eb5877257135308f7c5e923 microsoft.ml.onnxruntime.ep.webgpu.0.4.0.nupkg plugin)
    set(plugin_root "${CMAKE_CURRENT_BINARY_DIR}/onnxruntime-webgpu")
    onnx_extract("${plugin}" "${plugin_root}" "runtimes/${plugin_rid}/native/*" "LICENSE" "ThirdPartyNotices.txt")
    file(GLOB plugin_files "${plugin_root}/runtimes/${plugin_rid}/native/*")
    list(APPEND runtime_files ${plugin_files})
    list(APPEND license_files "onnxruntime-webgpu|${plugin_root}/LICENSE" "onnxruntime-webgpu|${plugin_root}/ThirdPartyNotices.txt")
elseif(AQUA_TARGET MATCHES "^android-")
    # The official Android package: an AAR with the C headers and one libonnxruntime.so per ABI.
    onnx_download(https://repo1.maven.org/maven2/com/microsoft/onnxruntime/onnxruntime-android/${onnx_version}/onnxruntime-android-${onnx_version}.aar
        e7fb945e402205f6db858d65bb78d2bdb0812317b383976c9e3bceb4862c73f1 onnxruntime-android-${onnx_version}.aar archive)
    set(root "${CMAKE_CURRENT_BINARY_DIR}/onnxruntime-android")
    onnx_extract("${archive}" "${root}" "headers/*" "jni/${ANDROID_ABI}/libonnxruntime.so")
    list(APPEND include_directories "${root}/headers")
    list(APPEND runtime_files "${root}/jni/${ANDROID_ABI}/libonnxruntime.so")
    list(APPEND sources src/platform/posix.c)
    list(APPEND link_libraries dl)
    # The AAR carries no license files; ONNX Runtime is MIT licensed like the desktop releases.
elseif(AQUA_TARGET STREQUAL "web")
    # ONNX Runtime built from its source release for WebAssembly and linked into the side module.
    include(cmake/onnxruntime-web.cmake)
    list(APPEND include_directories "${onnx_web_include}")
    list(APPEND link_libraries ${onnx_web_libraries})
    list(APPEND sources src/platform/web.c)
    list(APPEND license_files "onnxruntime|${source}/LICENSE" "onnxruntime|${source}/ThirdPartyNotices.txt")
endif()

aqua_add_library(onnx SOURCES ${sources} INCLUDE_DIRECTORIES ${include_directories} LINK_LIBRARIES ${link_libraries})
target_compile_definitions(aqua_onnx PRIVATE ONNX_RUNTIME_VERSION="${onnx_version}")
if(AQUA_TARGET STREQUAL "web")
    # libc++ for ONNX Runtime; calls between ONNX Runtime's own symbols (ONNX and protobuf export some) bind inside the
    # module, and the name section is dropped (about half the size).
    set_target_properties(aqua_onnx PROPERTIES LINKER_LANGUAGE CXX)
    target_link_options(aqua_onnx PRIVATE -Wl,-Bsymbolic -Wl,--strip-all)
endif()

# Runtime files sit next to the runtime (aquarius, aquarius.exe, libaquarius.so) or this external library, where
# src/platform looks for them, and install beside it for exported applications.
foreach(file IN LISTS runtime_files)
    file(COPY "${file}" DESTINATION "${CMAKE_BINARY_DIR}")
endforeach()
if(runtime_files)
    install(FILES ${runtime_files} DESTINATION .)
endif()
foreach(entry IN LISTS license_files)
    string(REPLACE "|" ";" entry "${entry}")
    list(GET entry 0 folder)
    list(GET entry 1 file)
    install(FILES "${file}" DESTINATION licenses/${folder})
endforeach()
