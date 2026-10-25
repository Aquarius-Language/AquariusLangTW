/* The desktop launcher (Windows and Linux): runs an application .wasm, or the application embedded in this executable
   by `aqua build --target windows|linux` (overlay format 2, compiler/AquariusPackaging/ExecutableBundle.cs).
     aquarius app.wasm [--entry script.aqua] [--documents file ...] [argument ...]
     aquarius --catalog        bilingual names and arities of every built-in module, as JSON (editor tooling)
     aquarius --runtime-info   this runtime pack's identity, as JSON (build targets)                                */
#include "desktop.h"
#include "../../host/host.h"
#include "sha256.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static bool (*window_wait)(double);
static void (*window_wake)(void);
void desktop_set_window_loop(bool (*wait)(double), void (*wake)(void)) { window_wait = wait; window_wake = wake; }
bool desktop_wait(double timeout_ms) { return window_wait && window_wait(timeout_ms); }
void desktop_wake(void) { if (window_wake) window_wake(); }

void platform_wait(aqua_runtime *rt, double timeout_ms);

static void say(int stream, const char *text) { platform_write(stream, text, strlen(text)); }

#define OVERLAY_FOOTER 64
static const char overlay_magic[16] = {'A', 'Q', 'U', 'A', 'R', 'I', 'U', 'S', '-', 'A', 'P', 'P', '-', 'V', '2', '!'};

/* The application appended to this executable, or NULL. `entry` receives the overlay's entry script, if any. */
static uint8_t *embedded_application(const uint8_t *file, size_t length, size_t *wasm_length, char **entry) {
    if (length < OVERLAY_FOOTER) return NULL;
    const uint8_t *footer = file + length - OVERLAY_FOOTER;
    if (memcmp(footer + 48, overlay_magic, 16) != 0) return NULL;
    int32_t version, metadata_length; int64_t wasm_size;
    memcpy(&version, footer, 4); memcpy(&metadata_length, footer + 4, 4); memcpy(&wasm_size, footer + 8, 8);
    if (version != 2 || metadata_length < 0 || metadata_length > 65536 || wasm_size <= 0 || wasm_size > 272ll * 1024 * 1024 ||
        (uint64_t)wasm_size + (uint64_t)metadata_length + OVERLAY_FOOTER >= length) return NULL;
    const uint8_t *wasm = footer - metadata_length - wasm_size;
    uint8_t digest[32];
    sha256(wasm, (size_t)(wasm_size + metadata_length), digest);
    if (memcmp(digest, footer + 16, 32) != 0) return NULL;
    /* Metadata: {"EntryPoint":"path.aqua"} */
    const char *metadata = (const char *)(wasm + wasm_size);
    const char *key = NULL;
    for (int32_t i = 0; i + 13 < metadata_length; i++) if (memcmp(metadata + i, "\"EntryPoint\"", 12) == 0) { key = metadata + i + 12; break; }
    if (key) {
        const char *end = metadata + metadata_length;
        while (key < end && *key != '"') key++;
        const char *start = ++key;
        while (key < end && *key != '"') key++;
        if (key < end) { *entry = malloc((size_t)(key - start) + 1); memcpy(*entry, start, (size_t)(key - start)); (*entry)[key - start] = 0; }
    }
    uint8_t *copy = malloc((size_t)wasm_size);
    memcpy(copy, wasm, (size_t)wasm_size);
    *wasm_length = (size_t)wasm_size;
    return copy;
}

static void json_string(const char *s) {
    say(1, "\"");
    for (; *s; s++) {
        if (*s == '"' || *s == '\\') { char e[2] = {'\\', *s}; platform_write(1, e, 2); }
        else platform_write(1, s, 1);
    }
    say(1, "\"");
}
static void json_names(const char *names) {
    say(1, "[");
    for (const char *p = names; *p;) {
        const char *end = strchr(p, '|');
        size_t n = end ? (size_t)(end - p) : strlen(p);
        char buffer[256];
        if (n >= sizeof buffer) n = sizeof buffer - 1;
        memcpy(buffer, p, n); buffer[n] = 0;
        if (p != names) say(1, ",");
        json_string(buffer);
        if (!end) break;
        p = end + 1;
    }
    say(1, "]");
}

/* Every module and class of this runtime's libraries; editor tooling and documentation read it. */
static int catalog(void) {
    static const char *const kinds[] = {"function", "property", "constant", "constant", "constant", "constant", "constant"};
    aqua_runtime *rt = NULL; (void)rt;
    extern void registry_ensure(void);
    registry_ensure();
    say(1, "{\"platform\":");
    json_string(AQUA_PLATFORM_NAME);
    say(1, ",\"libraries\":[");
    for (uint32_t l = 0; l < host_registry.library_count; l++) {
        aqua_library *library = host_registry.libraries[l];
        if (l) say(1, ",");
        say(1, "{\"name\":");
        json_string(library->name);
        say(1, ",\"modules\":[");
        bool first = true;
        for (uint32_t c = 0; c < host_registry.class_count; c++) {
            aqua_class *cls = host_registry.classes[c];
            if (cls->library != library) continue;
            if (!first) say(1, ",");
            first = false;
            say(1, "{\"names\":");
            json_names(cls->names);
            say(1, cls->module ? ",\"module\":true" : ",\"module\":false");
            say(1, ",\"members\":[");
            for (uint32_t m = 0; m < cls->member_count; m++) {
                const member *mm = &cls->members[m];
                char line[96];
                if (m) say(1, ",");
                say(1, "{\"names\":");
                json_names(mm->names);
                snprintf(line, sizeof line, ",\"kind\":\"%s\"", kinds[mm->kind]);
                say(1, line);
                if (mm->kind == MEMBER_FUNCTION) {
                    if (mm->maximum == UINT32_MAX) snprintf(line, sizeof line, ",\"minimum\":%u,\"maximum\":-1", mm->minimum);
                    else snprintf(line, sizeof line, ",\"minimum\":%u,\"maximum\":%u", mm->minimum, mm->maximum);
                    say(1, line);
                    say(1, ",\"signature\":");
                    json_string(mm->signature);
                }
                say(1, "}");
            }
            say(1, "]}");
        }
        say(1, "]}");
    }
    say(1, "]}\n");
    return 0;
}

static int runtime_info(void) {
    say(1, "{\"format\":\"aquarius-runtime-pack\",\"version\":2,\"platform\":\"" AQUA_PLATFORM_NAME "\",\"wasmAbiVersion\":3,\"overlayVersion\":2,\"apiVersion\":");
    char n[16]; snprintf(n, sizeof n, "%d", AQUA_API_VERSION); say(1, n);
    say(1, "}\n");
    return 0;
}

char *platform_full_path(const char *path);

static char *directory_of(const char *path) {
    char *copy = platform_full_path(path);
    for (char *p = copy; *p; p++) if (*p == '\\') *p = '/';
    char *slash = strrchr(copy, '/');
    if (slash) *slash = 0; else strcpy(copy, ".");
    return copy;
}

static int run(uint8_t *module, size_t length, const char *directory, const char *entry, const char *const *launch, uint32_t launch_count) {
    char *materialize = platform_temporary_directory("aquarius-resources");
    runtime_options options = {directory, entry, launch, launch_count, materialize};
    char *error = NULL;
    aqua_runtime *rt = runtime_create(module, length, &options, &error);
    free(materialize);
    if (!rt) {
        say(2, error ? error : "Cannot load the application.");
        say(2, "\n");
        free(error); free(module);
        return 1;
    }
    int code = 1;
    if (runtime_start(rt, entry)) {
        double wait;
        while ((wait = runtime_turn(rt, 50)) >= 0) if (wait > 0) platform_wait(rt, wait);
        code = rt->exit_code;
    }
    platform_flush();
    runtime_destroy(rt);
    return code;
}

int desktop_main(int count, char **arguments) {
    char *self = platform_executable_path();
    size_t self_length = 0;
    uint8_t *self_bytes = self ? platform_read_file(self, &self_length) : NULL;
    char *entry = NULL;
    size_t length = 0;
    uint8_t *module = self_bytes ? embedded_application(self_bytes, self_length, &length, &entry) : NULL;
    free(self_bytes);
    if (module) {
        /* A standalone application: every argument is a launch argument (documents opened with it). */
        char *directory = directory_of(self);
        int code = run(module, length, directory, entry, (const char *const *)arguments + 1, (uint32_t)(count > 1 ? count - 1 : 0));
        free(directory); free(entry); free(self);
        return code;
    }
    free(self);
    if (count < 2 || strcmp(arguments[1], "--help") == 0 || strcmp(arguments[1], "-h") == 0) {
        say(count < 2 ? 2 : 1, "aquarius app.wasm [--entry script.aqua] [--documents file ...] [argument ...]\naquarius --catalog | --runtime-info\n");
        return count < 2 ? 2 : 0;
    }
    if (strcmp(arguments[1], "--catalog") == 0) return catalog();
    if (strcmp(arguments[1], "--runtime-info") == 0) return runtime_info();
    const char *path = arguments[1];
    int next = 2;
    if (strcmp(path, "--entry") == 0 && count >= 4) { entry = arguments[2]; path = arguments[3]; next = 4; }
    else if (count >= 4 && strcmp(arguments[2], "--entry") == 0) { entry = arguments[3]; next = 4; }
    if (next < count && strcmp(arguments[next], "--documents") == 0) next++;
    module = platform_read_file(path, &length);
    if (!module) { say(2, "Cannot read "); say(2, path); say(2, "\n"); return 1; }
    char *directory = directory_of(path);
    int code = run(module, length, directory, entry, (const char *const *)arguments + next, (uint32_t)(count - next));
    free(directory);
    return code;
}
