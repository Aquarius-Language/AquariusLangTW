/* Terminal / 終端機 module: IsSupported, Run, RunScript and Platform, with the old runtime's validation, options,
 * results and messages. A run is a deferred call: the script waits while the platform layer's worker thread collects
 * the process's output, and the result arrives through aqua_post. */
#include "terminal.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define DEFAULT_TIMEOUT_MS 60000
#define MAXIMUM_TIMEOUT_MS 3600000

struct terminal_state {
    aqua_runtime *rt;
    terminal_job *jobs;        /* runs in progress, owned here until delivered */
    atomic_bool posted;        /* a delivery task is queued */
};

static aqua_class *result_class;

/* ---- Output buffers ---- */

void terminal_output_append(terminal_output *output, const char *bytes, size_t length) {
    if (output->full || length == 0) return;
    /* Keep whole UTF-8 sequences: a lead byte starts one UTF-16 unit (two for four-byte sequences). */
    size_t keep = 0;
    for (size_t i = 0; i < length; i++) {
        unsigned char c = (unsigned char)bytes[i];
        if ((c & 0xC0) != 0x80) {
            size_t units = c >= 0xF0 ? 2 : 1;
            if (output->units + units > TERMINAL_OUTPUT_LIMIT) { output->full = true; break; }
            output->units += units;
        }
        keep = i + 1;
    }
    if (output->length + keep + 1 > output->capacity) {
        size_t capacity = output->capacity ? output->capacity : 4096;
        while (capacity < output->length + keep + 1) capacity *= 2;
        output->data = realloc(output->data, capacity);
        output->capacity = capacity;
    }
    memcpy(output->data + output->length, bytes, keep);
    output->length += keep;
    output->data[output->length] = 0;
}

/* ---- Results ---- */

typedef struct result { int32_t exit_code; bool timed_out; aqua_ref output, error; } result;

static void result_finalize(aqua_runtime *rt, void *data) {
    result *r = data;
    aqua_release(rt, r->output);
    aqua_release(rt, r->error);
    free(r);
}
static result *self_result(aqua_call *call) { return aqua_self(call); }
static void result_exit_code(aqua_call *call) { aqua_return_int(call, self_result(call)->exit_code); }
static void result_output(aqua_call *call) { aqua_return(call, aqua_ref_value(aqua_call_runtime(call), self_result(call)->output)); }
static void result_error(aqua_call *call) { aqua_return(call, aqua_ref_value(aqua_call_runtime(call), self_result(call)->error)); }
static void result_timed_out(aqua_call *call) { aqua_return_bool(call, self_result(call)->timed_out); }

static aqua_value text_of(aqua_runtime *rt, const terminal_output *output) {
    return aqua_text(rt, output->data ? output->data : "", output->length);
}

/* ---- Jobs ---- */

static void job_free(terminal_job *job) {
    free(job->executable);
    for (uint32_t i = 0; i < job->count; i++) free(job->arguments[i]);
    free(job->arguments);
    free(job->directory);
    free(job->output.data);
    free(job->error_output.data);
    free(job->error);
    free(job);
}

/* Runs on the runtime's thread: settles every finished job. */
static void deliver(aqua_runtime *rt, void *data) {
    terminal_state *state = data;
    atomic_store(&state->posted, false);
    for (terminal_job **link = &state->jobs; *link;) {
        terminal_job *job = *link;
        if (!atomic_load(&job->done)) { link = &job->next; continue; }
        *link = job->next;
#if defined(TERMINAL_PROCESSES)
        terminal_platform_finish(job);
#endif
        if (job->error) {
            aqua_reject(job->pending, "Terminal / 終端機.%s: %s", job->function, job->error);
        } else {
            result *r = calloc(1, sizeof *r);
            r->exit_code = job->timed_out ? -1 : job->exit_code;
            r->timed_out = job->timed_out;
            r->output = aqua_retain(rt, text_of(rt, &job->output));
            r->error = aqua_retain(rt, text_of(rt, &job->error_output));
            aqua_resolve(job->pending, aqua_object_new(rt, result_class, r));
        }
        job_free(job);
    }
}

void terminal_job_done(terminal_job *job) {
    terminal_state *state = job->state;
    atomic_store(&job->done, true);
    if (!atomic_exchange(&state->posted, true)) aqua_post(state->rt, deliver, state);
}

static void *state_create(aqua_runtime *rt) {
    terminal_state *state = calloc(1, sizeof *state);
    state->rt = rt;
    return state;
}

static void state_destroy(aqua_runtime *rt, void *data) {
    (void)rt;
    terminal_state *state = data;
    for (terminal_job *job = state->jobs; job;) {
        terminal_job *next = job->next;
#if defined(TERMINAL_PROCESSES)
        if (atomic_load(&job->done)) terminal_platform_finish(job);
        else terminal_platform_cancel(job);
#endif
        job_free(job);
        job = next;
    }
    free(state);
}

/* ---- Validation ---- */

static void fail(aqua_call *call, const char *function, const char *message) {
    aqua_fail(call, "Terminal / 終端機.%s: %s", function, message);
}

/* Text(value) of the old runtime: a string without NUL characters, as malloc'd UTF-8. */
static char *text_value(aqua_call *call, aqua_value value, const char *function) {
    if (value.type == AQUA_STRING) {
        aqua_str s = aqua_str_of(call, value);
        if (!memchr(s.data, 0, s.length)) return strdup(s.data);
    }
    fail(call, function, "Expected a string without NUL characters.");
    return NULL;
}

static bool blank(const char *s) {
    for (; *s; s++) if (!strchr(" \t\n\r\v\f", *s)) return false;
    return true;
}

static bool same_prefix(const char *path, const char *prefix, size_t length) {
    for (size_t i = 0; i < length; i++) {
        char a = path[i], b = prefix[i];
        if (a >= 'A' && a <= 'Z') a = (char)(a + 32);
        if (b >= 'A' && b <= 'Z') b = (char)(b + 32);
        if (a != b) return false;
    }
    return true;
}

/* Paths naming a packaged asset (relative to the application, under it, or /bottles/...) become the asset's file, as
   the old runtime's resource mapper did; any other path stays as written. */
static char *resolve_path(aqua_call *call, const char *path) {
    aqua_runtime *rt = aqua_call_runtime(call);
    char *normal = strdup(path);
    for (char *p = normal; *p; p++) if (*p == '\\') *p = '/';
    const char *relative = normal;
    bool absolute = normal[0] == '/' || (normal[0] && normal[1] == ':');
    const char *root = aqua_application_directory(rt);
    size_t root_length = root ? strlen(root) : 0;
    while (root_length && root[root_length - 1] == '/') root_length--;
    if (same_prefix(normal, "/bottles/", 9)) relative = normal + 1;
    else if (absolute) {
        relative = root_length && strlen(normal) > root_length && normal[root_length] == '/' && same_prefix(normal, root, root_length)
            ? normal + root_length + 1 : NULL;
    }
    while (relative && relative[0] == '.' && relative[1] == '/') relative += 2;
    const char *file = relative ? aqua_asset_file(rt, relative) : NULL;
    char *resolved = strdup(file ? file : path);
    free(normal);
    return resolved;
}

/* The options hash: WorkingDirectory and TimeoutMs. */
static bool read_options(aqua_call *call, uint32_t index, terminal_job *job) {
    aqua_runtime *rt = aqua_call_runtime(call);
    if (aqua_argc(call) <= index) return true;
    aqua_value options = aqua_arg(call, index);
    if (options.type != AQUA_HASH) { fail(call, job->function, "Options must be a map."); return false; }
    uint32_t count = aqua_hash_count(rt, options);
    for (uint32_t i = 0; i < count; i++) {
        aqua_value key, value;
        aqua_hash_entry(rt, options, i, &key, &value);
        char *name = text_value(call, key, job->function);
        if (!name) return false;
        bool ok = true;
        if (strcmp(name, "WorkingDirectory") == 0) {
            char *directory = text_value(call, value, job->function);
            ok = directory != NULL;
            if (ok) { free(job->directory); job->directory = resolve_path(call, directory); free(directory); }
        } else if (strcmp(name, "TimeoutMs") == 0) {
            double n = value.number;
            ok = aqua_is_number(value) && n >= 1 && n <= MAXIMUM_TIMEOUT_MS && n == (double)(int32_t)n;
            if (ok) job->timeout_ms = (int32_t)n;
            else fail(call, job->function, "TimeoutMs must be an integer in 1..3600000.");
        } else {
            fail(call, job->function, "Unknown terminal option.");
            ok = false;
        }
        free(name);
        if (!ok) return false;
    }
    return true;
}

static void start(aqua_call *call, const char *function, bool script) {
#if !defined(TERMINAL_PROCESSES)
    (void)script;
    fail(call, function, "Terminal processes are unavailable on this platform.");
#else
    aqua_runtime *rt = aqua_call_runtime(call);
    terminal_job *job = calloc(1, sizeof *job);
    job->function = function;
    job->timeout_ms = DEFAULT_TIMEOUT_MS;
    uint32_t option_index;
    if (script) {
        char *text = text_value(call, aqua_arg(call, 0), function);
        if (!text) { job_free(job); return; }
        option_index = 1;
#if defined(TERMINAL_WINDOWS)
        static const char prefix[] = "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);$OutputEncoding=[Console]::OutputEncoding;";
        static const char *const switches[] = {"-NoLogo", "-NoProfile", "-NonInteractive", "-Command"};
        job->executable = strdup("powershell.exe");
        job->count = 5;
        job->arguments = calloc(job->count, sizeof *job->arguments);
        for (uint32_t i = 0; i < 4; i++) job->arguments[i] = strdup(switches[i]);
        job->arguments[4] = malloc(sizeof prefix + strlen(text));
        memcpy(job->arguments[4], prefix, sizeof prefix - 1);
        strcpy(job->arguments[4] + sizeof prefix - 1, text);
        free(text);
#else
        job->executable = strdup(strcmp(AQUA_PLATFORM_NAME, "android") == 0 ? "/system/bin/sh" : "/bin/sh");
        job->count = 2;
        job->arguments = calloc(job->count, sizeof *job->arguments);
        job->arguments[0] = strdup("-c");
        job->arguments[1] = text;
#endif
    } else {
        char *executable = text_value(call, aqua_arg(call, 0), function);
        if (!executable) { job_free(job); return; }
        option_index = 2;
        aqua_value list = aqua_arg(call, 1);
        if (list.type != AQUA_ARRAY) { free(executable); fail(call, function, "Arguments must be a string array."); job_free(job); return; }
        job->count = aqua_array_length(rt, list);
        job->arguments = calloc(job->count ? job->count : 1, sizeof *job->arguments);
        for (uint32_t i = 0; i < job->count; i++) {
            job->arguments[i] = text_value(call, aqua_array_get(rt, list, i), function);
            if (!job->arguments[i]) { free(executable); job_free(job); return; }
        }
        if (blank(executable)) { free(executable); fail(call, function, "Executable is required."); job_free(job); return; }
        if (strchr(executable, '/') || strchr(executable, '\\')) {
            job->executable = resolve_path(call, executable);
            free(executable);
        } else job->executable = executable;
    }
    if (!read_options(call, option_index, job)) { job_free(job); return; }
    terminal_state *state = aqua_state(call);
    job->state = state;
    char *error = NULL;
    if (!terminal_platform_start(job, &error)) {
        fail(call, function, error ? error : "Could not launch process.");
        free(error);
        job_free(job);
        return;
    }
    job->pending = aqua_defer(call);
    job->next = state->jobs;
    state->jobs = job;
#endif
}

/* ---- Module ---- */

static void is_supported(aqua_call *call) {
#if defined(TERMINAL_PROCESSES)
    aqua_return_bool(call, true);
#else
    aqua_return_bool(call, false);
#endif
}
static void run(aqua_call *call) { start(call, "Run", false); }
static void run_script(aqua_call *call) { start(call, "RunScript", true); }

AQUA_LIBRARY(terminal) {
    aqua_library_state(library, state_create, state_destroy);
    aqua_class *module = aqua_define_module(library, "Terminal|終端機");
    aqua_define_string(module, "Platform|平台", AQUA_PLATFORM_NAME);
    aqua_define_function(module, "IsSupported|是否支援", is_supported, "");
    /* Kinds are checked here, so the old messages (and the unsupported error first) stay as they were. */
    aqua_define_function(module, "Run|執行", run, "vv|v");
    aqua_define_function(module, "RunScript|執行腳本", run_script, "v|v");

    result_class = aqua_define_class(library, "Terminal.Result", result_finalize);
    aqua_define_property(result_class, "ExitCode|結束代碼", result_exit_code);
    aqua_define_property(result_class, "StandardOutput|標準輸出", result_output);
    aqua_define_property(result_class, "StandardError|標準錯誤", result_error);
    aqua_define_property(result_class, "TimedOut|已逾時", result_timed_out);
}
