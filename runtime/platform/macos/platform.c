/* The macOS layer of the host: the executable's path and the cache directory. The rest of a POSIX desktop is
   platform/posix (posix.c, desktop.c). */
#include <limits.h>
#include <mach-o/dyld.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "../../host/host.h"
#include "../desktop/desktop.h"

char *platform_executable_path(void) {
    uint32_t size = 0;
    _NSGetExecutablePath(NULL, &size);
    char *path = malloc(size + 1);
    if (!path || _NSGetExecutablePath(path, &size) != 0) { free(path); return NULL; }
    char *resolved = realpath(path, NULL); /* without the symbolic links and ./ a launch may have used */
    if (resolved) { free(path); return resolved; }
    return path;
}

/* ~/Library/Caches/aquarius, as Apple's file system guidelines place caches; $XDG_CACHE_HOME/aquarius when that is set,
   as command-line tools on macOS also honour it. */
char *platform_cache_directory(void) {
    const char *base = getenv("XDG_CACHE_HOME"), *suffix = "/aquarius";
    if (!base || base[0] != '/') { base = getenv("HOME"); suffix = "/Library/Caches/aquarius"; }
    if (!base || !*base) return NULL;
    size_t n = strlen(base) + strlen(suffix) + 1;
    char *path = malloc(n);
    snprintf(path, n, "%s%s", base, suffix);
    return path;
}
