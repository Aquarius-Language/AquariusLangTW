/* POSIX services shared by the Linux and Android layers: time, synchronization, threads, files and shared
   libraries. */
#define _GNU_SOURCE
#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <ftw.h>
#include <limits.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>
#include "posix.h"

/* ---- Time ---- */
double platform_now_ms(void) {
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return (double)t.tv_sec * 1000.0 + (double)t.tv_nsec / 1e6;
}
void platform_sleep(double ms) {
    if (ms <= 0) return;
    struct timespec t = {(time_t)(ms / 1000), (long)((ms - (double)(time_t)(ms / 1000) * 1000) * 1e6)};
    while (nanosleep(&t, &t) && errno == EINTR) {}
}

/* ---- Locks, signals and the loop's wake event ---- */
void *platform_lock_create(void) { pthread_mutex_t *m = malloc(sizeof *m); pthread_mutex_init(m, NULL); return m; }
void platform_lock(void *lock) { pthread_mutex_lock(lock); }
void platform_unlock(void *lock) { pthread_mutex_unlock(lock); }
void platform_lock_destroy(void *lock) { if (lock) { pthread_mutex_destroy(lock); free(lock); } }

typedef struct platform_signal { pthread_mutex_t mutex; pthread_cond_t condition; bool set; } platform_signal;
platform_signal *platform_signal_create(void) {
    platform_signal *s = calloc(1, sizeof *s);
    pthread_mutex_init(&s->mutex, NULL);
    pthread_condattr_t attributes;
    pthread_condattr_init(&attributes);
    pthread_condattr_setclock(&attributes, CLOCK_MONOTONIC);
    pthread_cond_init(&s->condition, &attributes);
    pthread_condattr_destroy(&attributes);
    return s;
}
void platform_signal_set(platform_signal *s) {
    pthread_mutex_lock(&s->mutex);
    s->set = true;
    pthread_cond_broadcast(&s->condition);
    pthread_mutex_unlock(&s->mutex);
}
static bool signal_wait(platform_signal *s, double timeout_ms, bool reset) {
    struct timespec deadline;
    clock_gettime(CLOCK_MONOTONIC, &deadline);
    if (timeout_ms >= 0) {
        long long ns = (long long)(timeout_ms * 1e6) + deadline.tv_nsec;
        deadline.tv_sec += (time_t)(ns / 1000000000); deadline.tv_nsec = (long)(ns % 1000000000);
    }
    pthread_mutex_lock(&s->mutex);
    while (!s->set) {
        if (timeout_ms < 0) pthread_cond_wait(&s->condition, &s->mutex);
        else if (pthread_cond_timedwait(&s->condition, &s->mutex, &deadline) == ETIMEDOUT) break;
    }
    bool was = s->set;
    if (reset) s->set = false;
    pthread_mutex_unlock(&s->mutex);
    return was;
}
bool platform_signal_wait(platform_signal *s, int32_t timeout_ms) { return signal_wait(s, timeout_ms, false); }
void platform_signal_destroy(platform_signal *s) {
    if (!s) return;
    pthread_cond_destroy(&s->condition); pthread_mutex_destroy(&s->mutex); free(s);
}

/* ---- Threads ---- */
typedef struct platform_thread { pthread_t thread; void (*run)(void *); void *data; platform_signal *done; bool joined; } platform_thread;
static void *thread_main(void *p) { platform_thread *t = p; t->run(t->data); platform_signal_set(t->done); return NULL; }
platform_thread *platform_thread_start(void (*run)(void *), void *data) {
    platform_thread *t = calloc(1, sizeof *t);
    t->run = run; t->data = data; t->done = platform_signal_create();
    pthread_attr_t attributes;
    pthread_attr_init(&attributes);
    pthread_attr_setstacksize(&attributes, 8u << 20);
    pthread_create(&t->thread, &attributes, thread_main, t);
    pthread_attr_destroy(&attributes);
    return t;
}
bool platform_thread_join(platform_thread *t, int32_t timeout_ms) {
    if (!platform_signal_wait(t->done, timeout_ms)) return false;
    if (!t->joined) { pthread_join(t->thread, NULL); t->joined = true; }
    return true;
}
void platform_thread_free(platform_thread *t) {
    if (!t) return;
    if (!t->joined) pthread_join(t->thread, NULL);
    platform_signal_destroy(t->done);
    free(t);
}

/* ---- Environment, files and processes ---- */
const char *platform_environment(const char *name) { return getenv(name); }

bool platform_write_file(const char *path, const uint8_t *data, size_t length) {
    int fd = open(path, O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC, 0644);
    if (fd < 0) return false;
    bool ok = true;
    while (length && ok) {
        ssize_t n = write(fd, data, length);
        if (n < 0 && errno == EINTR) continue;
        ok = n > 0;
        if (ok) { data += n; length -= (size_t)n; }
    }
    close(fd);
    return ok;
}
uint8_t *platform_read_file(const char *path, size_t *length) {
    int fd = open(path, O_RDONLY | O_CLOEXEC);
    if (fd < 0) return NULL;
    struct stat info;
    if (fstat(fd, &info) || info.st_size > (1ll << 31)) { close(fd); return NULL; }
    uint8_t *data = malloc((size_t)info.st_size + 1);
    size_t total = 0;
    while (total < (size_t)info.st_size) {
        ssize_t n = read(fd, data + total, (size_t)info.st_size - total);
        if (n < 0 && errno == EINTR) continue;
        if (n <= 0) break;
        total += (size_t)n;
    }
    close(fd);
    if (total != (size_t)info.st_size) { free(data); return NULL; }
    *length = total;
    return data;
}
bool platform_make_directories(const char *path) {
    char *copy = strdup(path);
    for (char *p = copy + 1; *p; p++) if (*p == '/') { *p = 0; mkdir(copy, 0755); *p = '/'; }
    bool ok = mkdir(copy, 0755) == 0 || errno == EEXIST;
    free(copy);
    return ok;
}
static int remove_entry(const char *path, const struct stat *info, int flag, struct FTW *walk) { (void)info; (void)flag; (void)walk; return remove(path); }
void platform_remove_tree(const char *path) { nftw(path, remove_entry, 16, FTW_DEPTH | FTW_PHYS); }
char *platform_temporary_directory(const char *prefix) {
    const char *base = getenv("TMPDIR");
    if (!base || !*base) base = "/tmp";
    size_t n = strlen(base) + strlen(prefix) + 48;
    char *path = malloc(n);
    snprintf(path, n, "%s/%s-%d-%lld", base, prefix, (int)getpid(), (long long)platform_now_ms());
    return path;
}
char *platform_full_path(const char *path) {
    char *full = realpath(path, NULL);
    return full ? full : strdup(path);
}
void *platform_library_open(const char *path, char **error) {
    void *library = dlopen(path, RTLD_NOW | RTLD_LOCAL);
    if (!library && error) *error = strdup(dlerror());
    return library;
}
void *platform_library_symbol(void *library, const char *name) { return dlsym(library, name); }

bool platform_signal_take(platform_signal *s, double timeout_ms) { return signal_wait(s, timeout_ms, true); }
