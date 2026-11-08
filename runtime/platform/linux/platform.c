/* The Linux layer of the host: the executable's path and the cache directory. The rest of a POSIX desktop is
   platform/posix (posix.c, desktop.c). */
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include "../../host/host.h"
#include "../desktop/desktop.h"

char *platform_executable_path(void) {
    char buffer[PATH_MAX];
    ssize_t n = readlink("/proc/self/exe", buffer, sizeof buffer - 1);
    if (n <= 0) return NULL;
    buffer[n] = 0;
    return strdup(buffer);
}

/* $XDG_CACHE_HOME/aquarius, by default ~/.cache/aquarius. */
char *platform_cache_directory(void) {
    const char *base = getenv("XDG_CACHE_HOME"), *suffix = "/aquarius";
    if (!base || base[0] != '/') { base = getenv("HOME"); suffix = "/.cache/aquarius"; }
    if (!base || !*base) return NULL;
    size_t n = strlen(base) + strlen(suffix) + 1;
    char *path = malloc(n);
    snprintf(path, n, "%s%s", base, suffix);
    return path;
}
