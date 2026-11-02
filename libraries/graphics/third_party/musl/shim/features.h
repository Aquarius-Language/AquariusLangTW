/* musl's internal features.h: hidden (visibility of the libm helpers). It is found before the C library's own
   <features.h>, which glibc's headers (stdint.h) need, so that one is included too where it exists. */
#if defined(__has_include_next)
#if __has_include_next(<features.h>)
#include_next <features.h>
#endif
#endif
#define hidden __attribute__((__visibility__("hidden")))
