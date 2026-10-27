/* The desktop launcher (Windows and Linux): runs an application .wasm, or the application embedded in this executable
   by `aqua build --target windows|linux` (overlay format 2, compiler/AquariusBuild/ExecutableBundle.cs).
     aquarius [--library file ...] app.wasm [--entry script.aqua] [--documents file ...] [argument ...]
     aquarius [--library file ...] --catalog   bilingual names and arities of every module, as JSON (editor tooling)
     aquarius --runtime-info   this runtime pack's identity, as JSON (build targets)                                */
#include "desktop.h"
#include "../../host/host.h"
#include "../../host/sha256.h"
#include "window_desktop.h"
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

static uint32_t overlay_hex4(const char **text, const char *end) {
    if (end - *text < 4) return UINT32_MAX;
    uint32_t value = 0;
    for (int i = 0; i < 4; i++) {
        unsigned char c = (unsigned char)*(*text)++;
        int digit = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
        if (digit < 0) return UINT32_MAX;
        value = (value << 4) | (uint32_t)digit;
    }
    return value;
}

/* Decode the overlay's JSON string, including UTF-16 escapes emitted by System.Text.Json. No runtime is started yet. */
static char *overlay_entry(const char *text, size_t length) {
    const char *end = text + length, *p = NULL;
    for (size_t i = 0; i + 12 <= length; i++) if (memcmp(text + i, "\"EntryPoint\"", 12) == 0) { p = text + i + 12; break; }
    if (!p) return NULL;
    while (p < end && (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n')) p++;
    if (p == end || *p++ != ':') return NULL;
    while (p < end && (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n')) p++;
    if (p == end || *p++ != '"') return NULL;
    char *result = malloc((size_t)(end - p) + 1), *out = result;
    if (!result) return NULL;
    while (p < end) {
        unsigned char c = (unsigned char)*p++;
        if (c == '"') { if (out == result) break; *out = 0; return result; }
        if (c < 0x20) break;
        if (c != '\\') { *out++ = (char)c; continue; }
        if (p == end) break;
        c = (unsigned char)*p++;
        if (c == 'u') {
            uint32_t code = overlay_hex4(&p, end);
            if (code >= 0xd800 && code <= 0xdbff) {
                if (end - p < 6 || p[0] != '\\' || p[1] != 'u') break;
                p += 2;
                uint32_t low = overlay_hex4(&p, end);
                if (low < 0xdc00 || low > 0xdfff) break;
                code = 0x10000 + ((code - 0xd800) << 10) + (low - 0xdc00);
            } else if (code >= 0xdc00 && code <= 0xdfff) break;
            if (code == 0 || code == UINT32_MAX) break; /* Entry paths cannot contain NUL. */
            if (code < 0x80) *out++ = (char)code;
            else if (code < 0x800) { *out++ = (char)(0xc0 | (code >> 6)); *out++ = (char)(0x80 | (code & 0x3f)); }
            else if (code < 0x10000) { *out++ = (char)(0xe0 | (code >> 12)); *out++ = (char)(0x80 | ((code >> 6) & 0x3f)); *out++ = (char)(0x80 | (code & 0x3f)); }
            else { *out++ = (char)(0xf0 | (code >> 18)); *out++ = (char)(0x80 | ((code >> 12) & 0x3f)); *out++ = (char)(0x80 | ((code >> 6) & 0x3f)); *out++ = (char)(0x80 | (code & 0x3f)); }
        } else if (c == '"' || c == '\\' || c == '/') *out++ = (char)c;
        else if (c == 'b') *out++ = '\b';
        else if (c == 'f') *out++ = '\f';
        else if (c == 'n') *out++ = '\n';
        else if (c == 'r') *out++ = '\r';
        else if (c == 't') *out++ = '\t';
        else break;
    }
    free(result);
    return NULL;
}

/* The application embedded in the executable `self`, or NULL; *damaged when an overlay is present but fails its checks.
   Only the overlay is read: the runtime before it is tens of megabytes. `entry` receives the overlay's entry script. */
static uint8_t *embedded_application(const char *self, size_t *wasm_length, char **entry, bool *damaged) {
    *damaged = false;
    size_t length = 0; uint64_t size = 0;
    uint8_t *footer = platform_read_tail(self, OVERLAY_FOOTER, &length, &size);
    if (!footer || length < OVERLAY_FOOTER || memcmp(footer + 48, overlay_magic, 16) != 0) { free(footer); return NULL; }
    *damaged = true;
    int32_t version, metadata_length; int64_t wasm_size;
    memcpy(&version, footer, 4); memcpy(&metadata_length, footer + 4, 4); memcpy(&wasm_size, footer + 8, 8);
    free(footer);
    if (version != 2 || metadata_length <= 0 || metadata_length > 65536 || wasm_size <= 0 || wasm_size > 272ll * 1024 * 1024 ||
        (uint64_t)wasm_size + (uint64_t)metadata_length + OVERLAY_FOOTER >= size) return NULL;
    size_t overlay = (size_t)wasm_size + (size_t)metadata_length + OVERLAY_FOOTER;
    uint8_t *wasm = platform_read_tail(self, overlay, &length, &size);
    if (!wasm || length != overlay) { free(wasm); return NULL; }
    footer = wasm + overlay - OVERLAY_FOOTER;
    uint8_t digest[32];
    sha256(wasm, (size_t)(wasm_size + metadata_length), digest);
    if (memcmp(digest, footer + 16, 32) != 0) { free(wasm); return NULL; }
    /* Metadata: {"EntryPoint":"path.aqua"} */
    *entry = overlay_entry((const char *)(wasm + wasm_size), (size_t)metadata_length);
    if (!*entry) { free(wasm); return NULL; }
    *damaged = false;
    *wasm_length = (size_t)wasm_size; /* the metadata and footer after it are not part of the module */
    return wasm;
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
    say(1, "{\"format\":\"aquarius-runtime-pack\",\"version\":2,\"platform\":\"" AQUA_PLATFORM_NAME "\",\"wasmAbiVersion\":4,\"overlayVersion\":2,\"apiVersion\":");
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

/* External libraries live in a libraries/ folder beside the application, or beside the runtime executable. */
bool platform_load_library(aqua_runtime *rt, const char *name, char **error) {
#if defined(_WIN32)
    const char *prefix = "aqua_", *suffix = ".dll";
#else
    const char *prefix = "libaqua_", *suffix = ".so";
#endif
    char *self = platform_executable_path();
    char *runtime_directory = self ? directory_of(self) : NULL;
    const char *directories[2] = {rt->application_directory, runtime_directory};
    bool loaded = false;
    char tried[2048] = "";
    for (int i = 0; i < 2 && !loaded; i++) {
        if (!directories[i] || !directories[i][0]) continue;
        char path[1200];
        snprintf(path, sizeof path, "%s/libraries/%s%s%s", directories[i], prefix, name, suffix);
        if (!platform_file_exists(path)) {
            size_t used = strlen(tried);
            snprintf(tried + used, sizeof tried - used, "%s%s", used ? ", " : "", path);
            continue;
        }
        loaded = library_load(path, error);
        if (!loaded) break;
    }
    if (!loaded && error && !*error) {
        char text[2300];
        snprintf(text, sizeof text, "%s%s%s was not found (looked for %s).", prefix, name, suffix, tried);
        *error = strdup(text);
    }
    free(self); free(runtime_directory);
    return loaded;
}

static int run(uint8_t *module, size_t length, const char *directory, const char *entry, const char *const *launch, uint32_t launch_count) {
    char *materialize = platform_temporary_directory("aquarius-resources");
    /* Desktop processes share no UI thread with a browser or Android's watchdog, so a run slice is 16 times the
       portable quantum: hot typed loops leave native code (and re-enter it) 16 times less often. Results are the same
       with any quantum; AQUARIUS_FUEL_QUANTUM overrides it (tests compare it with the portable one). */
    const char *quantum = platform_environment("AQUARIUS_FUEL_QUANTUM");
    runtime_options options = {directory, entry, launch, launch_count, materialize,
        quantum && *quantum ? (uint32_t)strtoul(quantum, NULL, 10) : 1048576u};
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
        /* Output is buffered within a turn and written after it, so a busy program's output (servers that never
           wait) still reaches pipes and files within one turn. */
        while ((wait = runtime_turn(rt, 50)) >= 0) {
            platform_flush();
            if (wait > 0) platform_wait(rt, wait);
        }
        code = rt->exit_code;
    }
    platform_flush();
    runtime_destroy(rt);
    return code;
}

int desktop_main(int count, char **arguments) {
    char *self = platform_executable_path();
    char *entry = NULL;
    size_t length = 0;
    bool damaged = false;
    uint8_t *module = self ? embedded_application(self, &length, &entry, &damaged) : NULL;
    if (damaged) {
        say(2, "This application is damaged: its embedded program does not match its checksum or format. Reinstall it.\n");
        free(self);
        return 1;
    }
    if (module) {
        /* A standalone application: every argument is a launch argument (documents opened with it). */
        char *directory = directory_of(self);
        int code = run(module, length, directory, entry, (const char *const *)arguments + 1, (uint32_t)(count > 1 ? count - 1 : 0));
        free(directory); free(entry); free(self);
        return code;
    }
    free(self);
    if (count < 2 || strcmp(arguments[1], "--help") == 0 || strcmp(arguments[1], "-h") == 0) {
        say(count < 2 ? 2 : 1, "aquarius [--library file ...] app.wasm [--entry script.aqua] [--documents file ...] [argument ...]\n"
            "aquarius [--library file ...] --catalog | --runtime-info\n");
        return count < 2 ? 2 : 0;
    }
    if (strcmp(arguments[1], "--catalog") == 0) return catalog();
    if (strcmp(arguments[1], "--runtime-info") == 0) return runtime_info();
    int first = 1;
    /* External native libraries to register before the application starts (aqua_<name>.dll, libaqua_<name>.so). */
    for (; first + 1 < count && strcmp(arguments[first], "--library") == 0; first += 2) {
        char *error = NULL;
        if (!library_load(arguments[first + 1], &error)) { say(2, error ? error : "Cannot load the library."); say(2, "\n"); free(error); return 1; }
    }
    /* aquarius --library file ... --catalog: the catalog including those external libraries. */
    if (first < count && strcmp(arguments[first], "--catalog") == 0) return catalog();
    if (first >= count) { say(2, "Missing the application .wasm file.\n"); return 2; }
    const char *path = arguments[first];
    int next = first + 1;
    if (strcmp(path, "--entry") == 0 && count >= first + 3) { entry = arguments[first + 1]; path = arguments[first + 2]; next = first + 3; }
    else if (count >= first + 3 && strcmp(arguments[first + 1], "--entry") == 0) { entry = arguments[first + 2]; next = first + 3; }
    if (next < count && strcmp(arguments[next], "--documents") == 0) next++;
    module = platform_read_file(path, &length);
    if (!module) { say(2, "Cannot read "); say(2, path); say(2, "\n"); return 1; }
    char *directory = directory_of(path);
    int code = run(module, length, directory, entry, (const char *const *)arguments + next, (uint32_t)(count - next));
    free(directory);
    return code;
}

void platform_runtime_closing(aqua_runtime *rt) { desktop_windows_close(rt); }
const uint8_t *platform_asset(aqua_runtime *rt, const char *name, size_t *length) { (void)rt; (void)name; (void)length; return NULL; }
