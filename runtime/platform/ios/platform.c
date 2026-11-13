/* The iOS and iPadOS layer of the host (POSIX for files, clocks, threads): window.m starts UIKit and calls ios_run on a
   thread of its own, which runs the application from the bundle's application.aquarius (the overlay ExecutableBundle
   writes) with the ahead-of-time engine (host/engine_aot.c), because iOS lets no application generate machine code.
   Output goes to the process's standard output and error (the simulator's console, Xcode's), whole lines at a time.
   The loop waits on a signal that platform_wake and UIKit's events set. */
#include <errno.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include "../../host/host.h"
#include "../../host/overlay.h"
#include "../posix/posix.h"
#include "ios.h"

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
/* Output goes to the browser console or the system log: plain text. */
bool platform_terminal_colors(int stream) { (void)stream; return false; }
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

/* ---- The loop's wake event ---- */
static platform_signal *wake_signal;
static pthread_once_t wake_once = PTHREAD_ONCE_INIT;
static void make_wake(void) { wake_signal = platform_signal_create(); }
void platform_wake(aqua_runtime *rt) { (void)rt; pthread_once(&wake_once, make_wake); platform_signal_set(wake_signal); }
void platform_wait(aqua_runtime *rt, double timeout_ms) {
    (void)rt;
    platform_flush();
    pthread_once(&wake_once, make_wake);
    if (!ios_events_pending()) platform_signal_take(wake_signal, timeout_ms >= 1e8 ? -1 : timeout_ms);
    ios_dispatch_events();
}

bool platform_execute(aqua_runtime *rt, const char *program, const char *const *arguments, uint32_t count, char **error) {
    (void)rt; (void)program; (void)arguments; (void)count;
    *error = strdup("execFile is not available on iOS.");
    return false;
}

/* Native libraries are linked into the application: iOS loads no others. */
bool platform_load_library(aqua_runtime *rt, const char *name, char **error) {
    (void)rt;
    char text[256];
    snprintf(text, sizeof text, "The library %s is not part of this application: iOS applications link their native libraries when they are built.", name);
    *error = strdup(text);
    return false;
}

static char *bundle_folder;
/* Assets stored beside the module are in the bundle's assets/ folder (aqua build --target ios). */
const uint8_t *platform_asset(aqua_runtime *rt, const char *name, size_t *length) {
    (void)rt;
    if (!bundle_folder) return NULL;
    char path[2048];
    snprintf(path, sizeof path, "%s/assets/%s", bundle_folder, name);
    return platform_read_file(path, length);
}

void platform_runtime_closing(aqua_runtime *rt) { ios_windows_close(rt); }

/* <sandbox>/Library/Caches/aquarius: the application's caches, which the system may clear. */
char *platform_cache_directory(void) {
    const char *home = getenv("HOME");
    if (!home || !*home) return NULL;
    size_t n = strlen(home) + 32;
    char *path = malloc(n);
    snprintf(path, n, "%s/Library/Caches/aquarius", home);
    return path;
}

int ios_run(const char *resources, const char *data) {
    bundle_folder = strdup(resources);
    char path[2048], materialize[2048];
    snprintf(path, sizeof path, "%s/application.aquarius", resources);
    snprintf(materialize, sizeof materialize, "%s/aquarius-resources", data);
    char *entry = NULL, *error = NULL;
    size_t length = 0;
    bool damaged = false;
    uint8_t *module = overlay_read(path, &length, &entry, &damaged);
    if (!module) error = strdup(damaged ? "This application is damaged: its program does not match its checksum or format. Reinstall it."
                                        : "The application bundle has no application.aquarius.");
    aqua_runtime *rt = module ? runtime_create(module, length, &(runtime_options){data, entry, NULL, 0, materialize}, &error) : NULL;
    if (!rt) {
        platform_write(2, error ? error : "Cannot load the application.", strlen(error ? error : "Cannot load the application."));
        platform_write(2, "\n", 1);
        platform_flush();
        free(error); free(entry);
        return 1;
    }
    int code = 1;
    if (runtime_start(rt, entry)) {
        double wait;
        while ((wait = runtime_turn(rt, 50)) >= 0) {
            platform_flush();
            ios_dispatch_events();
            if (wait > 0) platform_wait(rt, wait);
        }
        code = rt->exit_code;
    }
    platform_flush();
    runtime_destroy(rt);
    free(entry);
    return code;
}
