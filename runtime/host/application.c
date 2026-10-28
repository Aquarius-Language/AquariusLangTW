/* Application packages: a WebAssembly module whose `aquarius.application` custom section (ABI 4, written by the
   compiler) names its script modules, entry, supported platforms, assets, the import names of the Aquarius-source
   library modules it bundles and the native libraries it needs, with the asset bytes in an `aquarius.assets` section.
   Everything is validated before any code runs. */
#include "host.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define MAX_APPLICATION_BYTES (256u * 1024 * 1024)
#define PROGRAM_ADDRESS (4u * 1024 * 1024)

typedef struct reader { const uint8_t *data; size_t position, end; bool failed; } reader;

static uint8_t read_byte(reader *r) {
    if (r->position >= r->end) { r->failed = true; return 0; }
    return r->data[r->position++];
}
static uint32_t read_unsigned(reader *r) {
    uint32_t value = 0;
    for (int shift = 0; shift < 35; shift += 7) {
        uint8_t b = read_byte(r);
        if (shift == 28 && (b & 0xf0)) { r->failed = true; return 0; }
        value |= (uint32_t)(b & 0x7f) << shift;
        if (!(b & 0x80)) return value;
    }
    r->failed = true;
    return 0;
}
static int32_t read_signed(reader *r) {
    int32_t value = 0; int shift = 0; uint8_t b;
    do { b = read_byte(r); value |= (int32_t)(b & 0x7f) << shift; shift += 7; } while ((b & 0x80) && shift < 35 && !r->failed);
    if (shift < 32 && (b & 0x40)) value |= -(1 << shift);
    return value;
}
/* A length-prefixed UTF-8 name as a NUL-terminated copy. */
static char *read_string(reader *r) {
    uint32_t length = read_unsigned(r);
    if (r->failed || length > r->end - r->position) { r->failed = true; return NULL; }
    char *s = malloc(length + 1);
    memcpy(s, r->data + r->position, length);
    s[length] = 0;
    r->position += length;
    return s;
}

static bool fail(char **error, const char *message) {
    if (error) { *error = malloc(strlen(message) + 1); strcpy(*error, message); }
    return false;
}

static int lower(int c) { return c >= 'A' && c <= 'Z' ? c + 32 : c; }
static bool equal_ignoring_case(const char *a, const char *b) {
    while (*a && lower((unsigned char)*a) == lower((unsigned char)*b)) a++, b++;
    return *a == *b;
}
static bool ends_with_aqua(const char *path) {
    size_t n = strlen(path);
    return n >= 5 && equal_ignoring_case(path + n - 5, ".aqua");
}

/* Package paths are relative, '/'-separated and free of dot segments, reserved device names and characters Windows
   rejects, the same rule the compiler applies when packaging. */
static bool valid_path(const char *path) {
    size_t n = strlen(path);
    if (n == 0 || n > 1024 || path[0] == '/') return false;
    const char *segment = path;
    for (const char *p = path;; p++) {
        if (*p == '/' || *p == 0) {
            size_t length = (size_t)(p - segment);
            if (length == 0 || (length == 1 && segment[0] == '.') || (length == 2 && segment[0] == '.' && segment[1] == '.')) return false;
            if (segment[length - 1] == ' ' || segment[length - 1] == '.') return false;
            size_t stem = 0;
            while (stem < length && segment[stem] != '.') stem++;
            static const char *reserved[] = {"con", "prn", "aux", "nul"};
            char base[8] = {0};
            if (stem <= 4) for (size_t i = 0; i < stem; i++) base[i] = (char)lower((unsigned char)segment[i]);
            for (int i = 0; i < 4; i++) if (stem == 3 && memcmp(base, reserved[i], 3) == 0) return false;
            if (stem == 4 && (memcmp(base, "com", 3) == 0 || memcmp(base, "lpt", 3) == 0) && base[3] >= '1' && base[3] <= '9') return false;
            if (*p == 0) break;
            segment = p + 1;
            continue;
        }
        unsigned char c = (unsigned char)*p;
        if (c < 32 || c == '\\' || strchr(":*?\"<>|", c)) return false;
    }
    return true;
}

typedef struct section { uint8_t id; size_t content, end; const char *name; size_t name_length, payload; } section;

static bool next_section(const uint8_t *bytes, size_t length, size_t *position, section *s) {
    if (*position >= length) return false;
    reader r = {bytes, *position, length, false};
    s->id = read_byte(&r);
    uint32_t size = read_unsigned(&r);
    if (r.failed || size > length - r.position) { *position = length; s->id = 0xff; return true; }
    s->content = r.position; s->end = r.position + size; s->name = NULL; s->name_length = 0; s->payload = s->content;
    if (s->id == 0) {
        reader c = {bytes, s->content, s->end, false};
        uint32_t n = read_unsigned(&c);
        if (!c.failed && n <= s->end - c.position) { s->name = (const char *)bytes + c.position; s->name_length = n; s->payload = c.position + n; }
    }
    *position = s->end;
    return true;
}
static bool section_named(const section *s, const char *name) {
    return s->id == 0 && s->name && s->name_length == strlen(name) && memcmp(s->name, name, s->name_length) == 0;
}

bool application_parse(application *app, uint8_t *bytes, size_t length, char **error) {
    memset(app, 0, sizeof *app);
    app->bytes = bytes; app->length = length;
    static const uint8_t header[8] = {0, 'a', 's', 'm', 1, 0, 0, 0};
    if (length < 8 || length > MAX_APPLICATION_BYTES || memcmp(bytes, header, 8) != 0)
        return fail(error, "Expected a WebAssembly 1.0 application module.");
    section metadata = {0}, assets = {0}, s;
    bool has_metadata = false, has_assets = false;
    uint32_t data_segment_length = 0, program_functions = 0; bool has_program = false;
    for (size_t position = 8; next_section(bytes, length, &position, &s);) {
        if (s.id == 0xff) return fail(error, "Truncated WebAssembly section.");
        if (section_named(&s, "aquarius.application")) {
            if (has_metadata) return fail(error, "Duplicate Aquarius metadata.");
            metadata = s; has_metadata = true;
        } else if (section_named(&s, "aquarius.assets")) {
            if (has_assets) return fail(error, "Duplicate Aquarius assets.");
            assets = s; has_assets = true;
        } else if (s.id == 11) {
            /* The active data segment at the program address holds the function table, names and constant pools. */
            reader r = {bytes, s.content, s.end, false};
            for (uint32_t i = 0, count = read_unsigned(&r); i < count && !r.failed; i++) {
                uint32_t flags = read_unsigned(&r); int32_t address = -1;
                if (flags == 2) read_unsigned(&r);
                if (flags == 0 || flags == 2) {
                    if (read_byte(&r) != 0x41) break;
                    address = read_signed(&r);
                    if (read_byte(&r) != 0x0b) break;
                } else if (flags != 1) break;
                uint32_t n = read_unsigned(&r);
                if (r.failed || n > r.end - r.position) break;
                if ((uint32_t)address == PROGRAM_ADDRESS) {
                    data_segment_length = n; has_program = true;
                    if (n >= 4) memcpy(&program_functions, bytes + r.position, 4); /* Program.count: the function table's size */
                }
                r.position += n;
            }
        }
    }
    if (!has_metadata) return fail(error, "Missing or incompatible Aquarius WebAssembly ABI.");
    reader r = {bytes, metadata.payload, metadata.end, false};
    static const uint8_t magic[4] = {0, 'A', 'Q', 'M'};
    if (metadata.end - metadata.payload < 5 || memcmp(bytes + metadata.payload, magic, 4) != 0)
        return fail(error, "This application was built by an older AquariusLang compiler (ABI 2). Rebuild it from source.");
    r.position += 4;
    uint8_t abi = read_byte(&r);
    if (abi != 3 && abi != 4) return fail(error, "Missing or incompatible Aquarius WebAssembly ABI.");
    uint32_t value_abi = app->value_abi = read_unsigned(&r);
    app->program_address = read_unsigned(&r);
    app->heap_start = read_unsigned(&r);
    uint32_t function_layout = read_unsigned(&r);
    app->function_count = read_unsigned(&r);
    if (r.failed || (value_abi != 2 && value_abi != 3) || app->program_address != PROGRAM_ADDRESS || app->heap_start < app->program_address + 8 ||
        app->heap_start > MAX_APPLICATION_BYTES || function_layout != 40)
        return fail(error, "Invalid Aquarius value ABI or memory layout.");
    if (app->function_count == 0 || app->function_count > 100000) return fail(error, "Invalid application module count.");
    app->module_count = read_unsigned(&r);
    if (r.failed || app->module_count == 0 || app->module_count > 10000) return fail(error, "Invalid application module count.");
    app->modules = calloc(app->module_count, sizeof *app->modules);
    for (uint32_t i = 0; i < app->module_count; i++) {
        app->modules[i].path = read_string(&r);
        app->modules[i].function = read_unsigned(&r);
        if (r.failed || !valid_path(app->modules[i].path) || !ends_with_aqua(app->modules[i].path) || app->modules[i].function >= app->function_count)
            return fail(error, "Invalid application module.");
        for (uint32_t j = 0; j < i; j++) if (equal_ignoring_case(app->modules[j].path, app->modules[i].path)) return fail(error, "Invalid application module.");
    }
    app->entry = read_unsigned(&r);
    if (r.failed || app->entry >= app->module_count) return fail(error, "Missing application entry.");
    uint32_t platform_count = read_unsigned(&r);
    if (platform_count > 0) {
        app->platform_count = platform_count - 1;
        if (app->platform_count == 0 || app->platform_count > 32) return fail(error, "Invalid supported platform metadata.");
        app->platforms = calloc(app->platform_count, sizeof *app->platforms);
        for (uint32_t i = 0; i < app->platform_count; i++) {
            app->platforms[i] = read_string(&r);
            if (r.failed || !app->platforms[i][0] || strlen(app->platforms[i]) > 32) return fail(error, "Invalid supported platform metadata.");
        }
    }
    uint8_t storage = read_byte(&r);
    if (storage > 1) return fail(error, "Invalid asset storage.");
    app->external_assets = storage == 1;
    app->asset_count = read_unsigned(&r);
    if (r.failed || app->module_count + app->asset_count > 10000) return fail(error, "Invalid application module count.");
    app->assets = calloc(app->asset_count ? app->asset_count : 1, sizeof *app->assets);
    uint64_t total = 0;
    for (uint32_t i = 0; i < app->asset_count; i++) {
        app->assets[i].name = read_string(&r);
        app->assets[i].length = read_unsigned(&r);
        if (r.failed || !valid_path(app->assets[i].name) || ends_with_aqua(app->assets[i].name)) return fail(error, "Invalid application asset.");
        for (uint32_t j = 0; j < app->module_count; j++) if (equal_ignoring_case(app->modules[j].path, app->assets[i].name)) return fail(error, "Invalid application asset.");
        for (uint32_t j = 0; j < i; j++) if (equal_ignoring_case(app->assets[j].name, app->assets[i].name)) return fail(error, "Invalid application asset.");
        total += app->assets[i].length;
    }
    if (abi >= 4) {
        app->import_count = read_unsigned(&r);
        if (r.failed || app->import_count > 10000) return fail(error, "Invalid library module metadata.");
        app->imports = calloc(app->import_count ? app->import_count : 1, sizeof *app->imports);
        for (uint32_t i = 0; i < app->import_count; i++) {
            app->imports[i].name = read_string(&r);
            app->imports[i].module = read_unsigned(&r);
            if (r.failed || !app->imports[i].name[0] || strlen(app->imports[i].name) > 128 || app->imports[i].module >= app->module_count)
                return fail(error, "Invalid library module metadata.");
            for (uint32_t j = 0; j < i; j++) if (strcmp(app->imports[j].name, app->imports[i].name) == 0) return fail(error, "Invalid library module metadata.");
        }
        app->library_count = read_unsigned(&r);
        if (r.failed || app->library_count > 1000) return fail(error, "Invalid native library metadata.");
        app->libraries = calloc(app->library_count ? app->library_count : 1, sizeof *app->libraries);
        for (uint32_t i = 0; i < app->library_count; i++) {
            char *name = app->libraries[i] = read_string(&r);
            size_t n = name ? strlen(name) : 0;
            bool valid = !r.failed && n > 0 && n <= 64;
            for (size_t k = 0; valid && k < n; k++) valid = (name[k] >= 'a' && name[k] <= 'z') || (name[k] >= '0' && name[k] <= '9') || name[k] == '_';
            for (uint32_t j = 0; valid && j < i; j++) valid = strcmp(app->libraries[j], name) != 0;
            if (!valid) return fail(error, "Invalid native library metadata.");
        }
    }
    if (r.failed || r.position != r.end) return fail(error, "Invalid Aquarius metadata.");
    int64_t stored = has_assets ? (int64_t)(assets.end - assets.payload) : -1;
    if (app->external_assets ? stored >= 0 : app->asset_count == 0 ? stored >= 0 : stored != (int64_t)total)
        return fail(error, "Application assets do not match their metadata.");
    if (!app->external_assets) {
        size_t position = assets.payload;
        for (uint32_t i = 0; i < app->asset_count; i++) { app->assets[i].data = bytes + position; position += app->assets[i].length; }
    }
    if (!has_program || app->heap_start != app->program_address + data_segment_length || program_functions != app->function_count)
        return fail(error, "Application metadata does not match its memory layout.");
    return true;
}

void application_free(application *app) {
    for (uint32_t i = 0; i < app->module_count; i++) free(app->modules ? app->modules[i].path : NULL);
    for (uint32_t i = 0; i < app->platform_count; i++) free(app->platforms ? app->platforms[i] : NULL);
    for (uint32_t i = 0; i < app->asset_count; i++) free(app->assets ? app->assets[i].name : NULL);
    for (uint32_t i = 0; i < app->import_count; i++) free(app->imports ? app->imports[i].name : NULL);
    for (uint32_t i = 0; i < app->library_count; i++) free(app->libraries ? app->libraries[i] : NULL);
    free(app->modules); free(app->platforms); free(app->assets); free(app->imports); free(app->libraries); free(app->bytes);
    memset(app, 0, sizeof *app);
}

int32_t application_module(const application *app, const char *path) {
    for (uint32_t i = 0; i < app->module_count; i++) if (equal_ignoring_case(app->modules[i].path, path)) return (int32_t)i;
    return -1;
}

int32_t application_asset(const application *app, const char *path) {
    for (uint32_t i = 0; i < app->asset_count; i++) if (equal_ignoring_case(app->assets[i].name, path)) return (int32_t)i;
    return -1;
}

int32_t application_import(const application *app, const char *name) {
    for (uint32_t i = 0; i < app->import_count; i++) if (strcmp(app->imports[i].name, name) == 0) return (int32_t)app->imports[i].module;
    return -1;
}
