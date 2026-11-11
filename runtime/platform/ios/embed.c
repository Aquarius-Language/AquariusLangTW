/* The iOS layer's part of native library outputs (runtime/embed), in place of platform.c and window.m: a library links
   into any app or process, so it starts no UIKit application and opens no window. Its output goes to the process's
   standard output and error, whole lines at a time (the simulator's console, Xcode's); the loop waits on a POSIX signal.
   Assets and other native libraries are not available: the app that links the library owns its bundle. */
#include <errno.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include "../../host/host.h"
#include "../../embed/aqua/embed.h"
#include "../posix/posix.h"

/* ---- Output: whole lines ---- */
static char lines[2][4096];
static size_t line_length[2];
static pthread_mutex_t output_lock = PTHREAD_MUTEX_INITIALIZER;
static void emit(int s) {
    size_t n = line_length[s];
    const char *p = lines[s];
    while (n) { ssize_t w = write(s ? 2 : 1, p, n); if (w < 0 && errno == EINTR) continue; if (w <= 0) break; p += w; n -= (size_t)w; }
    line_length[s] = 0;
}
void platform_write(int stream, const char *utf8, size_t length) {
    int s = stream == 2 ? 1 : 0;
    pthread_mutex_lock(&output_lock);
    for (size_t i = 0; i < length; i++) {
        lines[s][line_length[s]++] = utf8[i];
        if (utf8[i] == '\n' || line_length[s] == sizeof lines[s]) emit(s);
    }
    pthread_mutex_unlock(&output_lock);
}
void platform_flush(void) {
    pthread_mutex_lock(&output_lock);
    for (int s = 0; s < 2; s++) if (line_length[s]) emit(s);
    pthread_mutex_unlock(&output_lock);
}
void embed_platform_flush(void) { platform_flush(); }

/* ---- The loop: a signal posted work sets ---- */
static platform_signal *wake_signal;
static pthread_once_t wake_once = PTHREAD_ONCE_INIT;
static void make_wake(void) { wake_signal = platform_signal_create(); }
void platform_wake(aqua_runtime *rt) { (void)rt; pthread_once(&wake_once, make_wake); platform_signal_set(wake_signal); }
void platform_wait(aqua_runtime *rt, double timeout_ms) {
    (void)rt;
    platform_flush();
    pthread_once(&wake_once, make_wake);
    platform_signal_take(wake_signal, timeout_ms >= 1e8 ? -1 : timeout_ms);
}

/* ---- What only an application has ---- */
bool platform_execute(aqua_runtime *rt, const char *program, const char *const *arguments, uint32_t count, char **error) {
    (void)rt; (void)program; (void)arguments; (void)count;
    *error = strdup("execFile is not available on iOS.");
    return false;
}
const uint8_t *platform_asset(aqua_runtime *rt, const char *name, size_t *length) { (void)rt; (void)name; (void)length; return NULL; }
void platform_runtime_closing(aqua_runtime *rt) { (void)rt; }
bool platform_load_library(aqua_runtime *rt, const char *name, char **error) {
    (void)rt;
    const char *text = "Native library outputs cannot load other native libraries yet: ";
    *error = malloc(strlen(text) + strlen(name) + 2);
    strcpy(*error, text); strcat(*error, name); strcat(*error, ".");
    return false;
}

/* <sandbox>/Library/Caches/aquarius in an app, $HOME/Library/Caches/aquarius elsewhere. */
char *platform_cache_directory(void) {
    const char *home = getenv("HOME");
    if (!home || !*home) return NULL;
    size_t n = strlen(home) + 32;
    char *path = malloc(n);
    snprintf(path, n, "%s/Library/Caches/aquarius", home);
    return path;
}
