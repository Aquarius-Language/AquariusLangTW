/* The Windows layer of the host: console output, time, synchronization, threads, files, processes and DLLs, on the
   Win32 API (UTF-8 at every boundary, UTF-16 inside Windows calls). */
#define WIN32_LEAN_AND_MEAN
#define UNICODE
#include <windows.h>
#include <shellapi.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "../../host/host.h"
#include "../desktop/desktop.h"

/* ---- UTF-8 <-> UTF-16 ---- */
wchar_t *windows_wide(const char *utf8) {
    int n = MultiByteToWideChar(CP_UTF8, 0, utf8, -1, NULL, 0);
    wchar_t *wide = malloc((size_t)(n > 0 ? n : 1) * sizeof *wide);
    if (n <= 0) { wide[0] = 0; return wide; }
    MultiByteToWideChar(CP_UTF8, 0, utf8, -1, wide, n);
    return wide;
}
char *windows_utf8(const wchar_t *wide) {
    int n = WideCharToMultiByte(CP_UTF8, 0, wide, -1, NULL, 0, NULL, NULL);
    char *utf8 = malloc((size_t)(n > 0 ? n : 1));
    if (n <= 0) { utf8[0] = 0; return utf8; }
    WideCharToMultiByte(CP_UTF8, 0, wide, -1, utf8, n, NULL, NULL);
    return utf8;
}

/* ---- Output: buffered, flushed before waiting and at exit ---- */
static char output[16384];
static size_t output_length;
static void write_handle(HANDLE h, const char *data, size_t length) {
    while (length) {
        DWORD written = 0, chunk = length > 1u << 20 ? 1u << 20 : (DWORD)length;
        if (!WriteFile(h, data, chunk, &written, NULL) || !written) return;
        data += written; length -= written;
    }
}
void platform_flush(void) {
    if (output_length) write_handle(GetStdHandle(STD_OUTPUT_HANDLE), output, output_length);
    output_length = 0;
}
void platform_write(int stream, const char *utf8, size_t length) {
    if (stream == 2) { platform_flush(); write_handle(GetStdHandle(STD_ERROR_HANDLE), utf8, length); return; }
    if (output_length + length > sizeof output) platform_flush();
    if (length > sizeof output) { write_handle(GetStdHandle(STD_OUTPUT_HANDLE), utf8, length); return; }
    memcpy(output + output_length, utf8, length);
    output_length += length;
}

/* ---- Time ---- */
double platform_now_ms(void) {
    static LARGE_INTEGER frequency;
    if (!frequency.QuadPart) QueryPerformanceFrequency(&frequency);
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    return (double)now.QuadPart * 1000.0 / (double)frequency.QuadPart;
}
void platform_sleep(double ms) { if (ms > 0) Sleep((DWORD)(ms < 1 ? 1 : ms)); }

/* ---- Locks, signals and the loop's wake event ---- */
void *platform_lock_create(void) { CRITICAL_SECTION *c = malloc(sizeof *c); InitializeCriticalSection(c); return c; }
void platform_lock(void *lock) { EnterCriticalSection(lock); }
void platform_unlock(void *lock) { LeaveCriticalSection(lock); }
void platform_lock_destroy(void *lock) { if (lock) { DeleteCriticalSection(lock); free(lock); } }

typedef struct platform_signal platform_signal;
platform_signal *platform_signal_create(void) { return (platform_signal *)CreateEventW(NULL, TRUE, FALSE, NULL); }
void platform_signal_set(platform_signal *s) { SetEvent((HANDLE)s); }
bool platform_signal_wait(platform_signal *s, int32_t timeout_ms) {
    return WaitForSingleObject((HANDLE)s, timeout_ms < 0 ? INFINITE : (DWORD)timeout_ms) == WAIT_OBJECT_0;
}
void platform_signal_destroy(platform_signal *s) { if (s) CloseHandle((HANDLE)s); }

static HANDLE wake_event;
void platform_wake(aqua_runtime *rt) {
    (void)rt;
    if (wake_event) SetEvent(wake_event);
    desktop_wake();
}
void platform_wait(aqua_runtime *rt, double timeout_ms) {
    (void)rt;
    platform_flush();
    if (desktop_wait(timeout_ms)) return; /* windows are open: their event loop waits */
    if (!wake_event) wake_event = CreateEventW(NULL, FALSE, FALSE, NULL);
    WaitForSingleObject(wake_event, timeout_ms >= 1e8 ? INFINITE : (DWORD)(timeout_ms < 1 ? 1 : timeout_ms));
}

/* ---- Threads ---- */
typedef struct platform_thread { HANDLE handle; void (*run)(void *); void *data; } platform_thread;
static DWORD WINAPI thread_main(void *p) { platform_thread *t = p; t->run(t->data); return 0; }
platform_thread *platform_thread_start(void (*run)(void *), void *data) {
    platform_thread *t = malloc(sizeof *t);
    t->run = run; t->data = data;
    t->handle = CreateThread(NULL, 8u << 20, thread_main, t, 0, NULL);
    return t;
}
bool platform_thread_join(platform_thread *t, int32_t timeout_ms) {
    return WaitForSingleObject(t->handle, timeout_ms < 0 ? INFINITE : (DWORD)timeout_ms) == WAIT_OBJECT_0;
}
void platform_thread_free(platform_thread *t) { if (t) { CloseHandle(t->handle); free(t); } }

/* ---- Environment, files and processes ---- */
/* The value stays valid for the life of the process. */
const char *platform_environment(const char *name) {
    wchar_t *wide = windows_wide(name);
    DWORD n = GetEnvironmentVariableW(wide, NULL, 0);
    if (!n) { free(wide); return NULL; }
    wchar_t *buffer = malloc(n * sizeof *buffer);
    GetEnvironmentVariableW(wide, buffer, n);
    free(wide);
    char *value = windows_utf8(buffer);
    free(buffer);
    return value;
}

bool platform_write_file(const char *path, const uint8_t *data, size_t length) {
    wchar_t *wide = windows_wide(path);
    HANDLE f = CreateFileW(wide, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    free(wide);
    if (f == INVALID_HANDLE_VALUE) return false;
    bool ok = true;
    while (length && ok) {
        DWORD written = 0, chunk = length > 1u << 30 ? 1u << 30 : (DWORD)length;
        ok = WriteFile(f, data, chunk, &written, NULL) && written;
        data += written; length -= written;
    }
    CloseHandle(f);
    return ok;
}
uint8_t *platform_read_file(const char *path, size_t *length) {
    wchar_t *wide = windows_wide(path);
    HANDLE f = CreateFileW(wide, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    free(wide);
    if (f == INVALID_HANDLE_VALUE) return NULL;
    LARGE_INTEGER size;
    if (!GetFileSizeEx(f, &size) || size.QuadPart > (1ll << 31)) { CloseHandle(f); return NULL; }
    uint8_t *data = malloc((size_t)size.QuadPart + 1);
    size_t total = 0;
    while (total < (size_t)size.QuadPart) {
        DWORD got = 0;
        if (!ReadFile(f, data + total, (DWORD)((size_t)size.QuadPart - total), &got, NULL) || !got) break;
        total += got;
    }
    CloseHandle(f);
    if (total != (size_t)size.QuadPart) { free(data); return NULL; }
    *length = total;
    return data;
}
bool platform_make_directories(const char *path) {
    char *copy = _strdup(path);
    for (char *p = copy + 1; *p; p++) {
        if (*p != '/' && *p != '\\') continue;
        char c = *p; *p = 0;
        wchar_t *wide = windows_wide(copy);
        CreateDirectoryW(wide, NULL);
        free(wide);
        *p = c;
    }
    wchar_t *wide = windows_wide(copy);
    bool ok = CreateDirectoryW(wide, NULL) || GetLastError() == ERROR_ALREADY_EXISTS;
    free(wide); free(copy);
    return ok;
}
void platform_remove_tree(const char *path) {
    wchar_t *wide = windows_wide(path);
    size_t n = wcslen(wide);
    wchar_t *list = calloc(n + 2, sizeof *list); /* SHFileOperation takes a double-NUL-terminated list */
    memcpy(list, wide, n * sizeof *list);
    for (size_t i = 0; i < n; i++) if (list[i] == L'/') list[i] = L'\\';
    SHFILEOPSTRUCTW operation = {0};
    operation.wFunc = FO_DELETE; operation.pFrom = list;
    operation.fFlags = FOF_NO_UI;
    SHFileOperationW(&operation);
    free(list); free(wide);
}
char *platform_temporary_directory(const char *prefix) {
    wchar_t base[MAX_PATH + 1];
    DWORD n = GetTempPathW(MAX_PATH, base);
    if (!n) return NULL;
    char *root = windows_utf8(base);
    size_t length = strlen(root) + strlen(prefix) + 32;
    char *path = malloc(length);
    snprintf(path, length, "%s%s-%lu-%llu", root, prefix, GetCurrentProcessId(), (unsigned long long)GetTickCount64());
    free(root);
    for (char *p = path; *p; p++) if (*p == '\\') *p = '/';
    return path;
}

/* Quotes one argument the way CommandLineToArgvW reads it back. */
static void append_argument(wchar_t **line, size_t *length, size_t *capacity, const wchar_t *argument) {
    size_t n = wcslen(argument);
    if (*length + n * 2 + 4 > *capacity) { *capacity = (*length + n * 2 + 4) * 2; *line = realloc(*line, *capacity * sizeof **line); }
    wchar_t *out = *line;
    if (*length) out[(*length)++] = L' ';
    bool quote = n == 0 || wcspbrk(argument, L" \t\"") != NULL;
    if (quote) out[(*length)++] = L'"';
    for (size_t i = 0; i < n; i++) {
        size_t slashes = 0;
        while (i < n && argument[i] == L'\\') { slashes++; i++; }
        if (i == n) { for (size_t k = 0; k < (quote ? slashes * 2 : slashes); k++) out[(*length)++] = L'\\'; break; }
        if (argument[i] == L'"') { for (size_t k = 0; k < slashes * 2 + 1; k++) out[(*length)++] = L'\\'; }
        else for (size_t k = 0; k < slashes; k++) out[(*length)++] = L'\\';
        out[(*length)++] = argument[i];
    }
    if (quote) out[(*length)++] = L'"';
    out[*length] = 0;
}

bool platform_execute(aqua_runtime *rt, const char *program, const char *const *arguments, uint32_t count, char **error) {
    (void)rt;
    size_t length = 0, capacity = 256;
    wchar_t *line = malloc(capacity * sizeof *line);
    line[0] = 0;
    wchar_t *wide = windows_wide(program);
    append_argument(&line, &length, &capacity, wide);
    free(wide);
    for (uint32_t i = 0; i < count; i++) { wide = windows_wide(arguments[i]); append_argument(&line, &length, &capacity, wide); free(wide); }
    STARTUPINFOW startup = {sizeof startup};
    PROCESS_INFORMATION process;
    platform_flush();
    if (!CreateProcessW(NULL, line, NULL, NULL, TRUE, 0, NULL, NULL, &startup, &process)) {
        char text[256];
        snprintf(text, sizeof text, "Cannot start %s (Windows error %lu).", program, GetLastError());
        *error = _strdup(text);
        free(line);
        return false;
    }
    free(line);
    WaitForSingleObject(process.hProcess, INFINITE);
    DWORD code = 1;
    GetExitCodeProcess(process.hProcess, &code);
    CloseHandle(process.hThread); CloseHandle(process.hProcess);
    return code == 0;
}

void *platform_library_open(const char *path, char **error) {
    wchar_t *wide = windows_wide(path);
    for (wchar_t *p = wide; *p; p++) if (*p == L'/') *p = L'\\';
    HMODULE module = LoadLibraryExW(wide, NULL, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    free(wide);
    if (!module && error) {
        char text[512];
        snprintf(text, sizeof text, "Cannot load %s (Windows error %lu).", path, GetLastError());
        *error = _strdup(text);
    }
    return module;
}
void *platform_library_symbol(void *library, const char *name) { return (void *)GetProcAddress(library, name); }

char *platform_full_path(const char *path) {
    wchar_t *wide = windows_wide(path);
    wchar_t full[32768];
    DWORD n = GetFullPathNameW(wide, 32768, full, NULL);
    free(wide);
    if (!n || n >= 32768) return _strdup(path);
    char *utf8 = windows_utf8(full);
    for (char *p = utf8; *p; p++) if (*p == '\\') *p = '/';
    return utf8;
}

/* ---- Process entry ---- */
char *platform_executable_path(void) {
    wchar_t path[32768];
    DWORD n = GetModuleFileNameW(NULL, path, 32768);
    if (!n || n >= 32768) return NULL;
    char *utf8 = windows_utf8(path);
    for (char *p = utf8; *p; p++) if (*p == '\\') *p = '/';
    return utf8;
}

int main(void) {
    SetConsoleOutputCP(CP_UTF8);
    int count = 0;
    wchar_t **wide = CommandLineToArgvW(GetCommandLineW(), &count);
    char **arguments = malloc((size_t)(count + 1) * sizeof *arguments);
    for (int i = 0; i < count; i++) arguments[i] = windows_utf8(wide[i]);
    arguments[count] = NULL;
    LocalFree(wide);
    int code = desktop_main(count, arguments);
    platform_flush();
    return code;
}
