/* The Linux layer of the host (POSIX): console output, time, synchronization, threads, files, processes and shared
   libraries. */
#define _GNU_SOURCE
#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <ftw.h>
#include <limits.h>
#include <pthread.h>
#include <spawn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>
#include "../../host/host.h"
#include "../desktop/desktop.h"
#include "../posix/posix.h"

extern char **environ;

/* ---- Output: buffered, flushed before waiting and at exit ---- */
static char output[16384];
static size_t output_length;
static void write_all(int fd, const char *data, size_t length) {
    while (length) {
        ssize_t n = write(fd, data, length);
        if (n < 0 && errno == EINTR) continue;
        if (n <= 0) return;
        data += n; length -= (size_t)n;
    }
}
void platform_flush(void) { if (output_length) write_all(1, output, output_length); output_length = 0; }
void platform_write(int stream, const char *utf8, size_t length) {
    if (stream == 2) { platform_flush(); write_all(2, utf8, length); return; }
    if (output_length + length > sizeof output) platform_flush();
    if (length > sizeof output) { write_all(1, utf8, length); return; }
    memcpy(output + output_length, utf8, length);
    output_length += length;
}

static platform_signal *wake_signal;
void platform_wake(aqua_runtime *rt) {
    (void)rt;
    if (wake_signal) platform_signal_set(wake_signal);
    desktop_wake();
}
void platform_wait(aqua_runtime *rt, double timeout_ms) {
    (void)rt;
    platform_flush();
    if (desktop_wait(timeout_ms)) return;
    if (!wake_signal) wake_signal = platform_signal_create();
    platform_signal_take(wake_signal, timeout_ms >= 1e8 ? -1 : timeout_ms);
}

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

bool platform_execute(aqua_runtime *rt, const char *program, const char *const *arguments, uint32_t count, char **error) {
    (void)rt;
    char **argv = calloc(count + 2, sizeof *argv);
    argv[0] = (char *)program;
    for (uint32_t i = 0; i < count; i++) argv[i + 1] = (char *)arguments[i];
    platform_flush();
    pid_t child;
    int status = posix_spawnp(&child, program, NULL, NULL, argv, environ);
    free(argv);
    if (status) {
        char text[512];
        snprintf(text, sizeof text, "Cannot start %s: %s.", program, strerror(status));
        *error = strdup(text);
        return false;
    }
    while (waitpid(child, &status, 0) < 0 && errno == EINTR) {}
    return WIFEXITED(status) && WEXITSTATUS(status) == 0;
}

int main(int count, char **arguments) {
    int code = desktop_main(count, arguments);
    platform_flush();
    return code;
}
