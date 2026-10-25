/* Windows services of the onnx library. Paths are UTF-8 in the library and UTF-16 at every Win32 call, so models and
 * runtime folders may use Traditional Chinese names. ONNX Runtime is loaded by full path only: System32 carries an
 * older onnxruntime.dll of Windows ML that must never be picked up instead.
 *
 * Windows 平台服務：以完整路徑載入 ONNX Runtime、UTF-16 路徑、CUDA 程式庫搜尋路徑。 */
#include <aqua/aqua.h>
#include "../platform.h"
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>
#include <wctype.h>

/* UTF-8 to UTF-16 with backslash separators (LoadLibraryEx requires them). */
static wchar_t *wide_path(const char *utf8) {
    int n = MultiByteToWideChar(CP_UTF8, 0, utf8, -1, NULL, 0);
    wchar_t *wide = malloc((size_t)(n > 0 ? n : 1) * sizeof *wide);
    if (!wide) return NULL;
    if (n <= 0) { wide[0] = 0; return wide; }
    MultiByteToWideChar(CP_UTF8, 0, utf8, -1, wide, n);
    for (wchar_t *p = wide; *p; p++) if (*p == L'/') *p = L'\\';
    return wide;
}

static char *utf8_of(const wchar_t *wide) {
    int n = WideCharToMultiByte(CP_UTF8, 0, wide, -1, NULL, 0, NULL, NULL);
    char *text = malloc((size_t)(n > 0 ? n : 1));
    if (!text) return NULL;
    if (n <= 0) { text[0] = 0; return text; }
    WideCharToMultiByte(CP_UTF8, 0, wide, -1, text, n, NULL, NULL);
    for (char *p = text; *p; p++) if (*p == '\\') *p = '/';
    return text;
}

static char *module_directory(HMODULE module) {
    wchar_t path[MAX_PATH * 8];
    DWORD n = GetModuleFileNameW(module, path, MAX_PATH * 8);
    if (n == 0 || n >= MAX_PATH * 8) return NULL;
    char *text = utf8_of(path);
    char *slash = text ? strrchr(text, '/') : NULL;
    if (slash) *slash = 0;
    return text;
}

char *onnx_platform_binary_directory(void) {
    HMODULE module = NULL;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            (LPCWSTR)(void *)&onnx_platform_binary_directory, &module)) return NULL;
    return module_directory(module);
}

char *onnx_platform_executable_directory(void) { return module_directory(NULL); }

bool onnx_platform_file_exists(const char *path) {
    wchar_t *wide = wide_path(path);
    DWORD attributes = wide ? GetFileAttributesW(wide) : INVALID_FILE_ATTRIBUTES;
    free(wide);
    return attributes != INVALID_FILE_ATTRIBUTES && !(attributes & FILE_ATTRIBUTE_DIRECTORY);
}

/* The system's message for an error code, in UTF-8 without the trailing line break. */
static void system_message(DWORD code, char *out, size_t size) {
    wchar_t *message = NULL;
    FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS, NULL, code,
                   0, (LPWSTR)&message, 0, NULL);
    char *text = message ? utf8_of(message) : NULL;
    if (message) LocalFree(message);
    size_t length = text ? strcspn(text, "\r\n") : 0;
    snprintf(out, size, "%.*s（錯誤 %lu）", (int)length, text ? text : "", (unsigned long)code);
    free(text);
}

static HMODULE runtime_library;

const OrtApiBase *onnx_platform_runtime(char *failure, size_t size) {
    if (!runtime_library) {
        char *directories[2] = {onnx_platform_binary_directory(), onnx_platform_executable_directory()};
        for (int i = 0; i < 2 && !runtime_library; i++) {
            if (!directories[i] || (i == 1 && directories[0] && _stricmp(directories[0], directories[1]) == 0)) continue;
            char path[4096];
            snprintf(path, sizeof path, "%s/onnxruntime.dll", directories[i]);
            if (!onnx_platform_file_exists(path)) continue;
            wchar_t *wide = wide_path(path);
            runtime_library = LoadLibraryExW(wide, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
            free(wide);
            if (!runtime_library) {
                DWORD code = GetLastError();
                char message[512];
                system_message(code, message, sizeof message);
                snprintf(failure, size, "無法載入 ONNX Runtime（%s）：%s%s", path, message,
                         code == ERROR_MOD_NOT_FOUND ? "。請確認已安裝 Microsoft Visual C++ 2015–2022 可轉散發套件（x64）。" : "");
                free(directories[0]); free(directories[1]);
                return NULL;
            }
        }
        free(directories[0]); free(directories[1]);
        if (!runtime_library) {
            snprintf(failure, size, "找不到 ONNX Runtime（onnxruntime.dll）；請將它放在程式旁。");
            return NULL;
        }
    }
    const OrtApiBase *(ORT_API_CALL *entry)(void) =
        (const OrtApiBase *(ORT_API_CALL *)(void))(void *)GetProcAddress(runtime_library, "OrtGetApiBase");
    if (!entry) {
        snprintf(failure, size, "onnxruntime.dll 不是有效的 ONNX Runtime 程式庫。");
        return NULL;
    }
    return entry();
}

void *onnx_platform_symbol(const char *name) {
    return runtime_library ? (void *)GetProcAddress(runtime_library, name) : NULL;
}

uint8_t *onnx_platform_read_file(const char *path, size_t limit, size_t *length, bool *too_large) {
    *too_large = false;
    wchar_t *wide = wide_path(path);
    FILE *file = wide ? _wfopen(wide, L"rb") : NULL;
    free(wide);
    if (!file) return NULL;
    uint8_t *bytes = NULL;
    if (_fseeki64(file, 0, SEEK_END) == 0) {
        long long size = _ftelli64(file);
        if (size >= 0 && (unsigned long long)size > limit) *too_large = true;
        else if (size >= 0 && _fseeki64(file, 0, SEEK_SET) == 0 && (bytes = malloc(size ? (size_t)size : 1)) != NULL) {
            if (fread(bytes, 1, (size_t)size, file) != (size_t)size) { free(bytes); bytes = NULL; }
            else *length = (size_t)size;
        }
    }
    fclose(file);
    return bytes;
}

/* ONNX Runtime converts plugin library paths to the ANSI code page, which fails for characters outside it (a
   Traditional Chinese folder on a non-Chinese system, or any name the code page lacks). Such paths are passed in their
   8.3 short form, which is ASCII where the volume keeps short names; a UTF-8 active-code-page manifest in the
   executable avoids the conversion problem entirely. */
ORTCHAR_T *onnx_platform_path(const char *utf8) {
    wchar_t *wide = wide_path(utf8);
    if (!wide) return NULL;
    BOOL lossy = FALSE;
    WideCharToMultiByte(CP_ACP, WC_NO_BEST_FIT_CHARS, wide, -1, NULL, 0, NULL, &lossy);
    if (!lossy) return wide;
    DWORD n = GetShortPathNameW(wide, NULL, 0);
    wchar_t *shortened = n ? malloc(n * sizeof *shortened) : NULL;
    DWORD written = shortened ? GetShortPathNameW(wide, shortened, n) : 0;
    if (written > 0 && written < n) {
        free(wide);
        return shortened;
    }
    free(shortened);
    return wide;
}

char *onnx_platform_data_directory(void) {
    wchar_t buffer[MAX_PATH * 4];
    DWORD n = GetEnvironmentVariableW(L"LOCALAPPDATA", buffer, MAX_PATH * 4);
    return n > 0 && n < MAX_PATH * 4 ? utf8_of(buffer) : NULL;
}

bool onnx_platform_make_directories(const char *path) {
    wchar_t *wide = wide_path(path);
    if (!wide) return false;
    for (wchar_t *p = wide + 1; ; p++) {
        if (*p == L'\\' || *p == 0) {
            wchar_t saved = *p;
            *p = 0;
            if (!(wcslen(wide) == 2 && wide[1] == L':')) CreateDirectoryW(wide, NULL);
            *p = saved;
            if (!saved) break;
        }
    }
    DWORD attributes = GetFileAttributesW(wide);
    free(wide);
    return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY);
}

/* Folders that hold any of NVIDIA's runtime libraries, from the roots the old desktop host searched: the absolute
   directories of AQUARIUS_ONNX_CUDA_PATH, then the program's folders and CUDA_PATH, each with the flat, toolkit and
   NVIDIA Python package layouts. Only this process's PATH changes; the working directory is never searched. */
void onnx_platform_prepare_cuda(void) {
    static bool configured;
    if (configured) return;
    configured = true;
    static const wchar_t *const relatives[] = {L"onnx-providers\\cuda", L"", L"bin\\x86_64", L"bin", L"cu13\\bin\\x86_64", L"cudnn\\bin"};
    static const wchar_t *const markers[] = {L"cublasLt64_13.dll", L"cudnn64_9.dll", L"cudart64_13.dll", L"nvrtc64_130_0.dll"};
    wchar_t roots[8192] = L"";
    wchar_t value[4096];
    DWORD n = GetEnvironmentVariableW(L"AQUARIUS_ONNX_CUDA_PATH", value, 4096);
    if (n > 0 && n < 4096) { wcscat(roots, value); wcscat(roots, L";"); }
    char *directories[2] = {onnx_platform_executable_directory(), onnx_platform_binary_directory()};
    for (int i = 0; i < 2; i++) {
        wchar_t *wide = directories[i] ? wide_path(directories[i]) : NULL;
        if (wide && wcslen(roots) + wcslen(wide) + 2 < 8192) { wcscat(roots, wide); wcscat(roots, L";"); }
        free(wide); free(directories[i]);
    }
    n = GetEnvironmentVariableW(L"CUDA_PATH", value, 4096);
    if (n > 0 && n < 4096 && wcslen(roots) + n + 2 < 8192) { wcscat(roots, value); wcscat(roots, L";"); }

    wchar_t found[16384] = L"";
    wchar_t *context = NULL;
    for (wchar_t *root = wcstok(roots, L";", &context); root; root = wcstok(NULL, L";", &context)) {
        while (*root == L' ') root++;
        size_t length = wcslen(root);
        while (length && root[length - 1] == L' ') root[--length] = 0;
        bool qualified = (iswalpha(root[0]) && root[1] == L':' && (root[2] == L'\\' || root[2] == L'/')) || (root[0] == L'\\' && root[1] == L'\\');
        if (!qualified) continue;
        for (size_t r = 0; r < sizeof relatives / sizeof *relatives; r++) {
            wchar_t joined[4096], directory[4096];
            swprintf(joined, 4096, L"%ls\\%ls", root, relatives[r]);
            DWORD full = GetFullPathNameW(joined, 4096, directory, NULL);
            if (full == 0 || full >= 4096) continue;
            size_t end = wcslen(directory);
            while (end > 3 && directory[end - 1] == L'\\') directory[--end] = 0;
            /* Skip folders already chosen (compared case-insensitively). */
            bool seen = false;
            wchar_t copy[16384];
            wcscpy(copy, found);
            wchar_t *inner = NULL;
            for (wchar_t *item = wcstok(copy, L";", &inner); item; item = wcstok(NULL, L";", &inner)) seen |= _wcsicmp(item, directory) == 0;
            if (seen) continue;
            bool holds = false;
            for (size_t m = 0; m < sizeof markers / sizeof *markers && !holds; m++) {
                wchar_t file[4200];
                swprintf(file, 4200, L"%ls\\%ls", directory, markers[m]);
                DWORD attributes = GetFileAttributesW(file);
                holds = attributes != INVALID_FILE_ATTRIBUTES && !(attributes & FILE_ATTRIBUTE_DIRECTORY);
            }
            if (holds && wcslen(found) + wcslen(directory) + 2 < 16384) { wcscat(found, directory); wcscat(found, L";"); }
        }
    }
    if (!found[0]) return;
    DWORD previous = GetEnvironmentVariableW(L"PATH", NULL, 0);
    size_t total = wcslen(found) + previous + 1;
    wchar_t *path = malloc(total * sizeof *path);
    if (!path) return;
    wcscpy(path, found);
    if (previous) GetEnvironmentVariableW(L"PATH", path + wcslen(found), previous);
    SetEnvironmentVariableW(L"PATH", path);
    free(path);
}

uint32_t onnx_platform_processor_count(void) {
    DWORD count = GetActiveProcessorCount(ALL_PROCESSOR_GROUPS);
    return count ? count : 1;
}

bool onnx_platform_emulator(void) { return false; }

static SRWLOCK engine_lock = SRWLOCK_INIT;
void onnx_platform_lock(void) { AcquireSRWLockExclusive(&engine_lock); }
void onnx_platform_unlock(void) { ReleaseSRWLockExclusive(&engine_lock); }
