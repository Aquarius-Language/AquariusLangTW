# The application library: Files, Paths, Images, Serialization|序列化, Storage, Clipboard, Application, Tasks and
# TextEdit. Builds inside the runtime (a default library) or on its own as an external library:
#   cmake -S . -B build -G Ninja -DCMAKE_TOOLCHAIN_FILE=<runtime/sdk/cmake/toolchains/<target>.cmake>
#
# Every codec, document format and compressor is C code compiled to the same algorithm on every target (the web
# included). The third-party C libraries are downloaded below from their official release URLs, checked against
# pinned SHA-256 hashes, and compiled into this library with private symbols (third_party/private_symbols.h).
cmake_minimum_required(VERSION 3.20)
project(aqua_application C)
include(AquariusLibrary)

set(AQUA_APPLICATION_DOWNLOADS "${CMAKE_CURRENT_BINARY_DIR}/downloads" CACHE PATH
    "Directory for the pinned dependency archives (point several build directories at one to download them once)")
set(third_party "${CMAKE_CURRENT_BINARY_DIR}/third_party")

# application_dependency(<name> <url> <sha256> [FILE]): downloads and verifies one pinned dependency and sets
# <name>_DIR to its unpacked source directory (archives) or to the directory holding the file (FILE).
function(application_dependency name url sha256)
    get_filename_component(file_name "${url}" NAME)
    if(file_name STREQUAL "download")
        get_filename_component(file_name "${url}" DIRECTORY)
        get_filename_component(file_name "${file_name}" NAME)
    endif()
    set(archive "${AQUA_APPLICATION_DOWNLOADS}/${name}-${file_name}")
    if(EXISTS "${archive}")
        file(SHA256 "${archive}" actual)
        if(NOT actual STREQUAL sha256)
            file(REMOVE "${archive}")
        endif()
    endif()
    if(NOT EXISTS "${archive}")
        message(STATUS "application: downloading ${url}")
        file(DOWNLOAD "${url}" "${archive}.part" EXPECTED_HASH SHA256=${sha256} STATUS status)
        list(GET status 0 code)
        if(code)
            file(REMOVE "${archive}.part")
            message(FATAL_ERROR "application: download of ${url} failed: ${status}")
        endif()
        file(RENAME "${archive}.part" "${archive}")
    endif()
    set(directory "${third_party}/${name}")
    if(ARGV3 STREQUAL "FILE")
        if(NOT EXISTS "${directory}/${file_name}")
            file(MAKE_DIRECTORY "${directory}")
            file(COPY "${archive}" DESTINATION "${directory}")
            file(RENAME "${directory}/${name}-${file_name}" "${directory}/${file_name}")
        endif()
    elseif(NOT EXISTS "${directory}/.unpacked-${sha256}")
        file(REMOVE_RECURSE "${directory}" "${directory}.unpack")
        file(ARCHIVE_EXTRACT INPUT "${archive}" DESTINATION "${directory}.unpack")
        file(GLOB top LIST_DIRECTORIES true "${directory}.unpack/*")
        file(RENAME "${top}" "${directory}")
        file(REMOVE_RECURSE "${directory}.unpack")
        file(TOUCH "${directory}/.unpacked-${sha256}")
    endif()
    set(${name}_DIR "${directory}" PARENT_SCOPE)
endfunction()

application_dependency(zlib https://github.com/madler/zlib/releases/download/v1.3.2/zlib-1.3.2.tar.gz
    bb329a0a2cd0274d05519d61c667c062e06990d72e125ee2dfa8de64f0119d16)
application_dependency(spng https://github.com/randy408/libspng/archive/refs/tags/v0.7.4.tar.gz
    47ec02be6c0a6323044600a9221b049f63e1953faf816903e7383d4dc4234487)
application_dependency(giflib https://downloads.sourceforge.net/project/giflib/giflib-5.x/giflib-5.2.2.tar.gz
    be7ffbd057cadebe2aa144542fd90c6838c6a083b5e8a9048b8ee3b66b29d5fb)
application_dependency(tiff https://download.osgeo.org/libtiff/tiff-4.7.2.tar.gz
    672bd7d10aee4606171afb864f3570b83340f6a33e2c186dc0512f7145ffdf6a)
application_dependency(brotli https://github.com/google/brotli/archive/refs/tags/v1.2.1.tar.gz
    98d35d11a78dc453aebb7d10c3748f3f73ff78c8267c0dc697b8631f67bf7e8f)
application_dependency(utf8proc https://github.com/JuliaStrings/utf8proc/archive/refs/tags/v2.12.0.tar.gz
    f564011d38b2888d583d510b08e69ffa15aa117155db1b9b49ef1dfe1fa25111)
# stb_image 2.30 and stb_image_write 1.16 at a pinned commit of the upstream repository (single-file libraries).
set(stb_commit 2c980bb59875b0d32144a71867fbdebb2f77cd20)
application_dependency(stb https://raw.githubusercontent.com/nothings/stb/${stb_commit}/stb_image.h
    594c2fe35d49488b4382dbfaec8f98366defca819d916ac95becf3e75f4200b3 FILE)
application_dependency(stb https://raw.githubusercontent.com/nothings/stb/${stb_commit}/stb_image_write.h
    cbd5f0ad7a9cf4468affb36354a1d2338034f2c12473cf1a8e32053cb6914a05 FILE)

# The third-party sources compiled into the library. Only what the codecs and compressors use: no file I/O parts of
# zlib, no JPEG/JBIG/LZMA/ZSTD/WebP/LERC codecs of libtiff (src/codecs/tiff_platform.c replaces tif_unix.c).
set(vendor_sources)
foreach(file adler32 compress crc32 deflate inffast inflate inftrees trees uncompr zutil)
    list(APPEND vendor_sources "${zlib_DIR}/${file}.c")
endforeach()
list(APPEND vendor_sources "${spng_DIR}/spng/spng.c")
foreach(file dgif_lib egif_lib gif_err gif_hash gifalloc openbsd-reallocarray quantize)
    list(APPEND vendor_sources "${giflib_DIR}/${file}.c")
endforeach()
foreach(file aux close codec color compress dir dirinfo dirread dirwrite dumpmode error extension fax3 fax3sm flush
        getimage hash_set luv lzw next open packbits pixarlog predict print read strip swab thunder tile version
        warning write zip)
    list(APPEND vendor_sources "${tiff_DIR}/libtiff/tif_${file}.c")
endforeach()
file(GLOB brotli_sources "${brotli_DIR}/c/common/*.c" "${brotli_DIR}/c/dec/*.c" "${brotli_DIR}/c/enc/*.c")
list(APPEND vendor_sources ${brotli_sources} "${utf8proc_DIR}/utf8proc.c")

set(vendor_includes "${zlib_DIR}" "${spng_DIR}/spng" "${giflib_DIR}" "${tiff_DIR}/libtiff"
    "${CMAKE_CURRENT_SOURCE_DIR}/third_party/config" "${brotli_DIR}/c/include" "${utf8proc_DIR}" "${stb_DIR}")
set(vendor_definitions SPNG_STATIC UTF8PROC_STATIC TIFF_DO_NOT_USE_NON_EXT_ALLOC_FUNCTIONS)
set(private_symbols "${CMAKE_CURRENT_SOURCE_DIR}/third_party/private_symbols.h")

add_library(application_vendor OBJECT ${vendor_sources})
target_include_directories(application_vendor PRIVATE ${vendor_includes})
target_compile_definitions(application_vendor PRIVATE ${vendor_definitions} DISABLE_CHECK_TIFFSWABMACROS)
set_source_files_properties("${tiff_DIR}/libtiff/tif_open.c" PROPERTIES COMPILE_DEFINITIONS ALLOW_TIFF_NON_EXT_ALLOC_FUNCTIONS)
target_compile_options(application_vendor PRIVATE -w -include "${private_symbols}")
set_target_properties(application_vendor PROPERTIES C_STANDARD 17 C_VISIBILITY_PRESET hidden POSITION_INDEPENDENT_CODE ON)

set(sources
    src/library.c src/support.c src/json.c src/text.c src/serialization.c src/compression.c src/archive.c
    src/paths.c src/textedit.c src/images.c src/files.c src/storage.c src/clipboard.c src/host.c
    src/codecs/png.c src/codecs/jpeg.c src/codecs/bmp.c src/codecs/gif.c src/codecs/tiff.c src/codecs/tiff_platform.c
    $<TARGET_OBJECTS:application_vendor>)
set(link_libraries)
if(AQUA_TARGET STREQUAL "web")
    list(APPEND sources src/platform/web.c)
elseif(AQUA_TARGET STREQUAL "windows-x64")
    list(APPEND sources src/files_native.c src/platform/windows.c src/platform/windows_shell.c)
    list(APPEND link_libraries ole32 oleaut32 uuid shell32 comdlg32 gdi32 user32 oleacc advapi32 comctl32 bcrypt)
elseif(AQUA_TARGET MATCHES "^android-")
    list(APPEND sources src/files_native.c src/platform/posix.c src/platform/android.c)
    list(APPEND link_libraries log m)
else()
    list(APPEND sources src/files_native.c src/platform/posix.c src/platform/linux.c)
    list(APPEND link_libraries m)
endif()

aqua_add_library(application SOURCES ${sources} INCLUDE_DIRECTORIES src ${vendor_includes}
    DEFINITIONS ${vendor_definitions} LINK_LIBRARIES ${link_libraries})
target_compile_options(aqua_application PRIVATE -include "${private_symbols}")
