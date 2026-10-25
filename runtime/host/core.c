/* The language's global functions every platform provides: print/印出, import/匯入, typeOf/型別, time, platform checks
   and execFile/執行檔案. Portable builtins (len, push, ...) are tokens the guest runtime executes itself. */
#include "host.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

bool platform_execute(aqua_runtime *rt, const char *program, const char *const *arguments, uint32_t count, char **error);

static void print(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    for (uint32_t i = 0; i < aqua_argc(call); i++) {
        if (i) platform_write(1, " ", 1);
        char *text = format_value(rt, aqua_arg(call, i));
        platform_write(1, text, strlen(text));
        free(text);
    }
    platform_write(1, "\n", 1);
}

static void import(aqua_call *call) {
    aqua_return(call, host_import(aqua_call_runtime(call), call->context, aqua_arg(call, 0)));
}

static void type_of(aqua_call *call) {
    aqua_value v = aqua_arg(call, 0);
    if (v.type != AQUA_NATIVE) { aqua_return_cstring(call, aqua_type_name(v)); return; }
    handle *h = handle_get(aqua_call_runtime(call), v.ref);
    if (!h || h->kind == HANDLE_BOUND) { aqua_return_cstring(call, "function"); return; }
    if (h->kind == HANDLE_MODULE) { aqua_return_cstring(call, "module"); return; }
    char name[96];
    names_first(h->cls->names, name, sizeof name);
    for (char *p = name; *p; p++) if (*p >= 'A' && *p <= 'Z') *p += 32;
    aqua_return_cstring(call, name);
}

static void current_time(aqua_call *call) { aqua_return_double(call, aqua_now_ms(aqua_call_runtime(call))); }

static void current_date(aqua_call *call) {
    time_t now = time(NULL);
    struct tm local;
#if defined(AQUA_OS_WINDOWS)
    localtime_s(&local, &now);
#else
    localtime_r(&now, &local);
#endif
    char text[16];
    strftime(text, sizeof text, "%Y-%m-%d", &local);
    aqua_return_cstring(call, text);
}

static void is_windows(aqua_call *call) { aqua_return_bool(call, strcmp(AQUA_PLATFORM_NAME, "windows") == 0); }
static void is_linux(aqua_call *call) { aqua_return_bool(call, strcmp(AQUA_PLATFORM_NAME, "linux") == 0); }
static void is_macos(aqua_call *call) { aqua_return_bool(call, strcmp(AQUA_PLATFORM_NAME, "macos") == 0); }
static void is_android(aqua_call *call) { aqua_return_bool(call, strcmp(AQUA_PLATFORM_NAME, "android") == 0); }
static void is_web(aqua_call *call) { aqua_return_bool(call, strcmp(AQUA_PLATFORM_NAME, "web") == 0); }

/* The directory of the calling script inside the application, without a trailing slash. */
static void working_directory(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    const char *script = call->context < rt->context_count ? rt->contexts[call->context].directory : "";
    const char *root = rt->application_directory;
    size_t n = strlen(root) + strlen(script) + 2;
    char *path = aqua_scratch(call, n);
    snprintf(path, n, "%s%s%s", root, *root && *script ? "/" : "", script);
    aqua_return_cstring(call, path);
}

/* A path into the application package names an asset; native programs get a real file for it. */
static const char *program_argument(aqua_call *call, const char *argument) {
    aqua_runtime *rt = aqua_call_runtime(call);
    size_t root = strlen(rt->application_directory);
    char *normal = aqua_scratch(call, strlen(argument) + 1);
    strcpy(normal, argument);
    for (char *p = normal; *p; p++) if (*p == '\\') *p = '/';
    if (root && strncmp(normal, rt->application_directory, root) == 0 && normal[root] == '/') {
        const char *file = aqua_asset_file(rt, normal + root + 1);
        if (file) return file;
    }
    return argument;
}

static void execute_file(aqua_call *call) {
#if defined(AQUA_DESKTOP)
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value list = aqua_arg(call, 1);
    uint32_t count = aqua_array_length(rt, list);
    const char **arguments = aqua_scratch(call, (count + 1) * sizeof *arguments);
    for (uint32_t i = 0; i < count; i++) {
        aqua_value item = aqua_array_get(rt, list, i);
        if (item.type != AQUA_STRING) { aqua_fail(call, "execFile / 執行檔案: arguments must be strings."); return; }
        arguments[i] = program_argument(call, aqua_str_of(call, item).data);
    }
    char *error = NULL;
    bool ok = platform_execute(rt, program_argument(call, aqua_arg_str(call, 0).data), arguments, count, &error);
    if (error) { aqua_fail(call, "execFile / 執行檔案: %s", error); free(error); return; }
    aqua_return_bool(call, ok);
#else
    aqua_fail(call, "execFile / 執行檔案 is not available on %s.", AQUA_PLATFORM_NAME);
#endif
}

extern aqua_class *thread_class;
void thread_finalize(aqua_runtime *rt, void *data);

void core_library(aqua_library *library) {
    aqua_class *globals = aqua_define_class(library, "globals", NULL);
    thread_class = aqua_define_class(library, "thread", thread_finalize);
    aqua_define_function(globals, "print|印出", print, "v*");
    aqua_define_function(globals, "import|匯入", import, "s");
    aqua_define_function(globals, "typeOf|型別", type_of, "v");
    aqua_define_function(globals, "currentTime|目前時間", current_time, "");
    aqua_define_function(globals, "currentDate|目前日期", current_date, "");
    aqua_define_function(globals, "isOSWindows|是Windows", is_windows, "");
    aqua_define_function(globals, "isOSLinux|是Linux", is_linux, "");
    aqua_define_function(globals, "isOSMacOS|是MacOS", is_macos, "");
    aqua_define_function(globals, "isOSAndroid|是Android", is_android, "");
    aqua_define_function(globals, "isWeb|是網頁", is_web, "");
    aqua_define_function(globals, "execFile|執行檔案", execute_file, "sa");
    aqua_define_property(globals, "currWorkingDir|目前工作目錄", working_directory);
    /* Portable builtins: the guest runtime executes these ids itself (runtime/guest/runtime.c rt_call). */
    static const char *const portable[] = {"len|長度", "last|最後一個", "rest|其餘", "push|加入", "floodFill|區域填色"};
    for (uint32_t i = 0; i < 5; i++) { aqua_value token = {AQUA_BUILTIN, i + 1, 0}; class_define_value(globals, portable[i], token); }
}
