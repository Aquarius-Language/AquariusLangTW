/* Windows file system primitives (wide-character Win32 calls on UTF-8 paths) and known folders. Replacing or moving
 * a file retries briefly while an on-access scanner holds it (the old TransientFileLocks: 12 attempts, 25 ms steps). */
#define WIN32_LEAN_AND_MEAN
#include "platform.h"
#include <windows.h>
#include <shlobj.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

const char app_os_separator = '\\';

/* ---- Text and errors ---- */

static wchar_t *wide(const char *utf8) {
    int n = MultiByteToWideChar(CP_UTF8, 0, utf8, -1, NULL, 0);
    wchar_t *w = malloc((size_t)(n > 0 ? n : 1) * sizeof *w);
    if (n <= 0 || !MultiByteToWideChar(CP_UTF8, 0, utf8, -1, w, n)) w[0] = 0;
    return w;
}

static char *narrow(const wchar_t *w) {
    int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, NULL, 0, NULL, NULL);
    char *s = malloc((size_t)(n > 0 ? n : 1));
    if (n <= 0 || !WideCharToMultiByte(CP_UTF8, 0, w, -1, s, n, NULL, NULL)) s[0] = 0;
    return s;
}

/* A path for the wide APIs: long absolute paths get the \\?\ prefix. */
static wchar_t *path_of(const char *utf8) {
    wchar_t *w = wide(utf8);
    size_t n = wcslen(w);
    if (n < 240 || wcsncmp(w, L"\\\\?\\", 4) == 0) return w;
    bool unc = n > 2 && w[0] == L'\\' && w[1] == L'\\';
    wchar_t *prefixed = malloc((n + 8) * sizeof *prefixed);
    if (unc) { wcscpy(prefixed, L"\\\\?\\UNC\\"); wcscat(prefixed, w + 2); }
    else { wcscpy(prefixed, L"\\\\?\\"); wcscat(prefixed, w); }
    free(w);
    return prefixed;
}

static bool fail(app_error *e, DWORD code, const char *path) {
    switch (code) {
    case ERROR_FILE_NOT_FOUND: return app_error_set(e, APP_NOT_FOUND, "Could not find file '%s'.", path);
    case ERROR_PATH_NOT_FOUND: case ERROR_INVALID_DRIVE: case ERROR_BAD_NETPATH: case ERROR_BAD_NET_NAME:
        return app_error_set(e, APP_NOT_FOUND, "Could not find a part of the path '%s'.", path);
    case ERROR_ACCESS_DENIED: return app_error_set(e, APP_PERMISSION_DENIED, "Access to the path '%s' is denied.", path);
    case ERROR_SHARING_VIOLATION: case ERROR_LOCK_VIOLATION:
        return app_error_set(e, APP_UNAVAILABLE, "The process cannot access the file '%s' because it is being used by another process.", path);
    case ERROR_FILE_EXISTS: case ERROR_ALREADY_EXISTS: return app_error_set(e, APP_UNAVAILABLE, "The file '%s' already exists.", path);
    case ERROR_DIR_NOT_EMPTY: return app_error_set(e, APP_UNAVAILABLE, "The directory is not empty. : '%s'", path);
    case ERROR_DISK_FULL: case ERROR_HANDLE_DISK_FULL: return app_error_set(e, APP_UNAVAILABLE, "There is not enough space on the disk. : '%s'", path);
    default: {
        wchar_t message[512] = {0};
        FormatMessageW(FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS, NULL, code, 0, message, 511, NULL);
        for (size_t n = wcslen(message); n && (message[n - 1] == L'\r' || message[n - 1] == L'\n' || message[n - 1] == L' '); n--) message[n - 1] = 0;
        char *text = narrow(message);
        app_error_set(e, APP_UNAVAILABLE, "%s : '%s'", text, path);
        free(text);
        return false;
    }
    }
}

static bool transient(DWORD code, bool access_denied) {
    return code == 32 || code == 33 || code == 1175 || code == 1176 || code == 1177 || (access_denied && code == ERROR_ACCESS_DENIED);
}

/* ---- Paths and metadata ---- */

char *app_os_full_path(const char *path, app_error *e) {
    if (!*path) { app_error_set(e, APP_INVALID_DATA, "The path is empty. (Parameter 'path')"); return NULL; }
    wchar_t *w = wide(path);
    DWORD n = GetFullPathNameW(w, 0, NULL, NULL);
    wchar_t *full = malloc((n + 1) * sizeof *full);
    if (!n || !GetFullPathNameW(w, n + 1, full, NULL)) { free(w); free(full); fail(e, GetLastError(), path); return NULL; }
    free(w);
    char *result = narrow(full);
    free(full);
    return result;
}

static double filetime_ms(FILETIME t) {
    ULARGE_INTEGER v = {.LowPart = t.dwLowDateTime, .HighPart = t.dwHighDateTime};
    return (double)(v.QuadPart - 116444736000000000ull) / 10000.0;
}

bool app_os_stat(const char *path, app_stat *out, app_error *e) {
    (void)e;
    memset(out, 0, sizeof *out);
    out->length = -1;
    wchar_t *w = path_of(path);
    WIN32_FILE_ATTRIBUTE_DATA data;
    BOOL ok = GetFileAttributesExW(w, GetFileExInfoStandard, &data);
    free(w);
    if (!ok) return true; /* missing, or not reachable: does not exist (as FileInfo.Exists) */
    out->exists = true;
    out->kind = data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY ? APP_DIRECTORY : APP_FILE;
    if (out->kind == APP_FILE) out->length = (int64_t)((uint64_t)data.nFileSizeHigh << 32 | data.nFileSizeLow);
    out->modified_ms = filetime_ms(data.ftLastWriteTime);
    out->has_modified = true;
    return true;
}

bool app_os_is_link(const char *path) {
    wchar_t *w = path_of(path);
    DWORD attributes = GetFileAttributesW(w);
    free(w);
    return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_REPARSE_POINT);
}

static int compare_names(const void *a, const void *b) { return strcmp(*(char *const *)a, *(char *const *)b); }

bool app_os_list(const char *path, char ***names, size_t *count, app_error *e) {
    *names = NULL;
    *count = 0;
    char *pattern = app_printf("%s%s*", path, path[0] && (path[strlen(path) - 1] == '\\' || path[strlen(path) - 1] == '/') ? "" : "\\");
    wchar_t *w = path_of(pattern);
    free(pattern);
    WIN32_FIND_DATAW found;
    HANDLE find = FindFirstFileExW(w, FindExInfoBasic, &found, FindExSearchNameMatch, NULL, FIND_FIRST_EX_LARGE_FETCH);
    free(w);
    if (find == INVALID_HANDLE_VALUE) {
        DWORD code = GetLastError();
        if (code == ERROR_FILE_NOT_FOUND) return true;
        return fail(e, code == ERROR_FILE_NOT_FOUND ? ERROR_PATH_NOT_FOUND : code, path);
    }
    size_t capacity = 16;
    char **list = malloc(capacity * sizeof *list);
    do {
        if (wcscmp(found.cFileName, L".") == 0 || wcscmp(found.cFileName, L"..") == 0) continue;
        if (*count == capacity) { capacity *= 2; list = realloc(list, capacity * sizeof *list); }
        list[(*count)++] = narrow(found.cFileName);
    } while (FindNextFileW(find, &found));
    FindClose(find);
    qsort(list, *count, sizeof *list, compare_names);
    *names = list;
    return true;
}

bool app_os_mkdirs(const char *path, app_error *e) {
    char *copy = app_strdup(path);
    size_t n = strlen(copy);
    /* Skip the root ("C:\", "\\server\share\"). */
    size_t start = 0;
    if (n >= 2 && copy[1] == ':') start = 3;
    else if (n >= 2 && copy[0] == '\\' && copy[1] == '\\') {
        int slashes = 0;
        for (size_t i = 2; i < n; i++) if (copy[i] == '\\' && ++slashes == 2) { start = i + 1; break; }
        if (!start) start = n;
    }
    bool ok = true;
    for (size_t i = start; i <= n && ok; i++) {
        if (i < n && copy[i] != '\\' && copy[i] != '/') continue;
        char saved = copy[i];
        copy[i] = 0;
        if (copy[0]) {
            wchar_t *w = path_of(copy);
            if (!CreateDirectoryW(w, NULL)) {
                DWORD code = GetLastError();
                DWORD attributes = GetFileAttributesW(w);
                if (code != ERROR_ALREADY_EXISTS || attributes == INVALID_FILE_ATTRIBUTES || !(attributes & FILE_ATTRIBUTE_DIRECTORY)) {
                    ok = code == ERROR_ALREADY_EXISTS
                             ? app_error_set(e, APP_UNAVAILABLE, "Cannot create '%s' because a file or directory with the same name already exists.", copy)
                             : fail(e, code, copy);
                }
            }
            free(w);
        }
        copy[i] = saved;
    }
    free(copy);
    return ok;
}

bool app_os_remove_file(const char *path, app_error *e) {
    wchar_t *w = path_of(path);
    BOOL ok = DeleteFileW(w);
    DWORD code = GetLastError();
    free(w);
    if (ok || code == ERROR_FILE_NOT_FOUND) return true;
    return fail(e, code, path);
}

bool app_os_remove_directory(const char *path, app_error *e) {
    wchar_t *w = path_of(path);
    BOOL ok = RemoveDirectoryW(w);
    DWORD code = GetLastError();
    free(w);
    return ok || fail(e, code == ERROR_FILE_NOT_FOUND ? ERROR_PATH_NOT_FOUND : code, path);
}

bool app_os_rename(const char *from, const char *to, bool overwrite, app_error *e) {
    wchar_t *a = path_of(from), *b = path_of(to);
    DWORD attributes = GetFileAttributesW(a), code = 0;
    bool directory = attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY);
    bool ok = false;
    for (int attempt = 1; attempt <= 12; attempt++) {
        if (MoveFileExW(a, b, (overwrite ? MOVEFILE_REPLACE_EXISTING : 0) | (directory ? 0 : MOVEFILE_COPY_ALLOWED))) { ok = true; break; }
        code = GetLastError();
        if (attempt == 12 || !transient(code, directory)) break;
        Sleep(25 * attempt);
    }
    free(a);
    free(b);
    if (ok) return true;
    if (code == ERROR_FILE_EXISTS || code == ERROR_ALREADY_EXISTS) return app_error_set(e, APP_UNAVAILABLE, "Cannot create a file when that file already exists. : '%s'", to);
    return fail(e, code, code == ERROR_FILE_NOT_FOUND ? from : to);
}

bool app_os_replace(const char *stage, const char *target, app_error *e) {
    wchar_t *a = path_of(stage), *b = path_of(target);
    DWORD code = 0;
    bool ok = false;
    for (int attempt = 1; attempt <= 12; attempt++) {
        if (ReplaceFileW(b, a, NULL, REPLACEFILE_IGNORE_MERGE_ERRORS, NULL, NULL)) { ok = true; break; }
        code = GetLastError();
        if (attempt == 12 || !transient(code, false)) break;
        Sleep(25 * attempt);
    }
    free(a);
    free(b);
    return ok || fail(e, code, target);
}

bool app_os_write_new(const char *path, const uint8_t *data, size_t length, app_error *e) {
    wchar_t *w = path_of(path);
    HANDLE file = CreateFileW(w, GENERIC_WRITE, 0, NULL, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, NULL);
    free(w);
    if (file == INVALID_HANDLE_VALUE) return fail(e, GetLastError(), path);
    bool ok = true;
    while (ok && length) {
        DWORD part = length > 0x40000000 ? 0x40000000 : (DWORD)length, written = 0;
        ok = WriteFile(file, data, part, &written, NULL) && written == part;
        data += part;
        length -= part;
    }
    if (ok) ok = FlushFileBuffers(file);
    DWORD code = GetLastError();
    CloseHandle(file);
    if (!ok) { wchar_t *x = path_of(path); DeleteFileW(x); free(x); return fail(e, code, path); }
    return true;
}

bool app_os_same_path(const char *a, const char *b) {
    wchar_t *x = wide(a), *y = wide(b);
    bool same = CompareStringOrdinal(x, -1, y, -1, TRUE) == CSTR_EQUAL;
    free(x);
    free(y);
    return same;
}

char *app_os_temporary_directory(void) {
    wchar_t buffer[MAX_PATH + 2];
    DWORD n = GetTempPathW(MAX_PATH + 1, buffer);
    return n ? narrow(buffer) : app_strdup("C:\\Windows\\Temp\\");
}

/* ---- Streams ---- */

struct app_os_file { HANDLE handle; int mode; int64_t append_start; char *path; };

app_os_file *app_os_open(const char *path, int mode, app_error *e) {
    static const DWORD dispositions[] = {OPEN_EXISTING, CREATE_ALWAYS, OPEN_EXISTING, OPEN_ALWAYS};
    wchar_t *w = path_of(path);
    HANDLE handle = CreateFileW(w, mode == APP_OPEN_READ ? GENERIC_READ : GENERIC_WRITE, FILE_SHARE_READ, NULL, dispositions[mode], FILE_ATTRIBUTE_NORMAL, NULL);
    free(w);
    if (handle == INVALID_HANDLE_VALUE) { fail(e, GetLastError(), path); return NULL; }
    app_os_file *f = calloc(1, sizeof *f);
    f->handle = handle;
    f->mode = mode;
    f->path = app_strdup(path);
    if (mode == APP_OPEN_APPEND) {
        LARGE_INTEGER zero = {0}, end;
        SetFilePointerEx(handle, zero, &end, FILE_END);
        f->append_start = end.QuadPart;
    }
    return f;
}

int64_t app_os_read(app_os_file *f, void *buffer, size_t count, app_error *e) {
    DWORD read = 0;
    if (!ReadFile(f->handle, buffer, (DWORD)count, &read, NULL)) {
        DWORD code = GetLastError();
        if (code == ERROR_HANDLE_EOF) return 0;
        fail(e, code, f->path);
        return -1;
    }
    return read;
}

bool app_os_write(app_os_file *f, const void *data, size_t count, app_error *e) {
    const uint8_t *p = data;
    while (count) {
        DWORD part = count > 0x40000000 ? 0x40000000 : (DWORD)count, written = 0;
        if (!WriteFile(f->handle, p, part, &written, NULL) || written != part) return fail(e, GetLastError(), f->path);
        p += part;
        count -= part;
    }
    return true;
}

int64_t app_os_seek(app_os_file *f, int64_t offset, int origin, app_error *e) {
    LARGE_INTEGER current = {0}, size = {0}, zero = {0};
    SetFilePointerEx(f->handle, zero, &current, FILE_CURRENT);
    GetFileSizeEx(f->handle, &size);
    int64_t base = origin == APP_SEEK_BEGIN ? 0 : origin == APP_SEEK_CURRENT ? current.QuadPart : size.QuadPart;
    int64_t target = base + offset;
    if (target < 0) { app_error_set(e, APP_UNAVAILABLE, "An attempt was made to move the position before the beginning of the stream."); return -1; }
    if (f->mode == APP_OPEN_APPEND && target < f->append_start) {
        app_error_set(e, APP_UNAVAILABLE, "Unable seek backward to overwrite data that previously existed in a file opened in Append mode.");
        return -1;
    }
    LARGE_INTEGER to = {.QuadPart = target}, result;
    if (!SetFilePointerEx(f->handle, to, &result, FILE_BEGIN)) { fail(e, GetLastError(), f->path); return -1; }
    return result.QuadPart;
}

bool app_os_flush(app_os_file *f, app_error *e) {
    if (f->mode == APP_OPEN_READ) return true;
    return FlushFileBuffers(f->handle) || fail(e, GetLastError(), f->path);
}

void app_os_close(app_os_file *f) {
    if (!f) return;
    CloseHandle(f->handle);
    free(f->path);
    free(f);
}

/* ---- Known folders ---- */

static char *known_folder(const KNOWNFOLDERID *id, const char *application) {
    wchar_t *folder = NULL;
    if (SHGetKnownFolderPath(id, KF_FLAG_DEFAULT, NULL, &folder) != S_OK) { CoTaskMemFree(folder); return NULL; }
    char *base = narrow(folder);
    CoTaskMemFree(folder);
    char *path = app_printf("%s\\%s", base, application);
    free(base);
    return path;
}

char *app_os_settings_directory(const char *application) { return known_folder(&FOLDERID_RoamingAppData, application); }
char *app_os_data_directory(const char *application) { return known_folder(&FOLDERID_LocalAppData, application); }
