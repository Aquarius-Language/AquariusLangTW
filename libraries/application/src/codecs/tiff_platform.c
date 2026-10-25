/* libtiff's platform layer for the application library, in place of tif_unix.c / tif_win32.c: memory allocation and
 * the process-wide message handlers. Images are read from memory (TIFFClientOpenExt), so there is no file I/O, and
 * messages go to each handle's own handlers (tiff.c) instead of stderr. */
#include <stdlib.h>
#include <string.h>
#include <tiffio.h>

void *_TIFFmalloc(tmsize_t s) { return s == 0 ? NULL : malloc((size_t)s); }
void *_TIFFcalloc(tmsize_t count, tmsize_t size) { return count == 0 || size == 0 ? NULL : calloc((size_t)count, (size_t)size); }
void _TIFFfree(void *p) { free(p); }
void *_TIFFrealloc(void *p, tmsize_t s) { return realloc(p, (size_t)s); }
void _TIFFmemset(void *p, int v, tmsize_t c) { memset(p, v, (size_t)c); }
void _TIFFmemcpy(void *d, const void *s, tmsize_t c) { memcpy(d, s, (size_t)c); }
int _TIFFmemcmp(const void *a, const void *b, tmsize_t c) { return memcmp(a, b, (size_t)c); }

TIFFErrorHandler _TIFFwarningHandler = NULL;
TIFFErrorHandler _TIFFerrorHandler = NULL;
