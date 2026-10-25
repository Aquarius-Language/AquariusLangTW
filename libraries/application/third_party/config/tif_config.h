/* libtiff 4.7.2 build configuration for the application library (replaces the generated tif_config.h). The library
   reads images from memory through TIFFClientOpenExt, so no file I/O platform layer is compiled. */
#ifndef AQUA_APPLICATION_TIF_CONFIG_H
#define AQUA_APPLICATION_TIF_CONFIG_H

#include "tiffconf.h"

#define CCITT_SUPPORT 1
#define CHECK_JPEG_YCBCR_SUBSAMPLING 1
#define CHUNKY_STRIP_READ_SUPPORT 1
#define HAVE_ASSERT_H 1
#define HAVE_FCNTL_H 1
#define HAVE_SYS_TYPES_H 1
#if !defined(_WIN32)
#define HAVE_UNISTD_H 1
#endif
#define PACKAGE "LibTIFF Software"
#define PACKAGE_BUGREPORT "tiff@lists.osgeo.org"
#define PACKAGE_NAME "LibTIFF Software"
#define PACKAGE_TARNAME "tiff"
#define PACKAGE_URL ""
#define SIZEOF_SIZE_T __SIZEOF_SIZE_T__
#define STRIP_SIZE_DEFAULT 8192
#define TIFF_MAX_DIR_COUNT 1048576
#define WORDS_BIGENDIAN 0

#if !defined(__MINGW32__)
#define TIFF_SIZE_FORMAT "zu"
#endif
#if SIZEOF_SIZE_T == 8
#define TIFF_SSIZE_FORMAT PRId64
#if defined(__MINGW32__)
#define TIFF_SIZE_FORMAT PRIu64
#endif
#elif SIZEOF_SIZE_T == 4
#define TIFF_SSIZE_FORMAT PRId32
#if defined(__MINGW32__)
#define TIFF_SIZE_FORMAT PRIu32
#endif
#else
#error "Unsupported size_t size"
#endif

#endif
