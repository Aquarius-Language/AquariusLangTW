/* The process-wide registry of libraries, their modules and classes, and each class's members. Registration happens
   once per library (default libraries at startup, external ones when loaded); runtimes create module instances and
   per-runtime state lazily. */
#include "host.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

registry host_registry;
extern const aqua_api_table host_api_table;
char *utf16_to_utf8(const uint16_t *units, size_t count, size_t *length);
size_t utf8_to_utf16(const char *text, size_t length, uint16_t *out);

static char *copy(const char *s) { size_t n = strlen(s) + 1; char *c = malloc(n); memcpy(c, s, n); return c; }

/* Whether one alias of a '|'-separated list equals `name`. */
bool names_contain(const char *names, const char *name) {
    size_t n = strlen(name);
    for (const char *p = names; *p;) {
        const char *end = strchr(p, '|');
        size_t length = end ? (size_t)(end - p) : strlen(p);
        if (length == n && memcmp(p, name, n) == 0) return true;
        if (!end) break;
        p = end + 1;
    }
    return false;
}

const char *names_first(const char *names, char *buffer, size_t size) {
    const char *end = strchr(names, '|');
    size_t length = end ? (size_t)(end - names) : strlen(names);
    if (length >= size) length = size - 1;
    memcpy(buffer, names, length);
    buffer[length] = 0;
    return buffer;
}

static aqua_library *new_library(const char *name) {
    aqua_library *library = calloc(1, sizeof *library);
    library->api = &host_api_table;
    library->name = copy(name);
    library->index = host_registry.library_count;
    host_registry.libraries = realloc(host_registry.libraries, (host_registry.library_count + 1) * sizeof *host_registry.libraries);
    host_registry.libraries[host_registry.library_count++] = library;
    return library;
}

/* ---- Member lookup by the guest's UTF-16 names ----
   Guest member reads name members by Text. Each class's aliases are indexed by their UTF-16 units when its library has
   registered, so a read hashes the name in place instead of converting it and scanning every alias. A class that gained
   members afterwards falls back to the scan. */
static uint32_t units_hash(const uint16_t *units, uint32_t count) {
    uint32_t h = 2166136261u;
    for (uint32_t i = 0; i < count; i++) h = (h ^ units[i]) * 16777619u;
    return h;
}

static void index_class(aqua_class *c) {
    uint32_t aliases = 0;
    for (uint32_t i = 0; i < c->member_count; i++) for (const char *p = c->members[i].names; p; p = strchr(p, '|'), p = p ? p + 1 : NULL) aliases++;
    uint32_t capacity = 16;
    while (capacity < aliases * 2) capacity *= 2;
    member_name *table = calloc(capacity, sizeof *table);
    for (uint32_t i = 0; i < c->member_count; i++)
        for (const char *p = c->members[i].names; p;) {
            const char *end = strchr(p, '|');
            size_t length = end ? (size_t)(end - p) : strlen(p);
            uint16_t *units = malloc(length * 2 + 2);
            uint32_t count = (uint32_t)utf8_to_utf16(p, length, units);
            uint32_t at = units_hash(units, count) & (capacity - 1);
            /* The first member with an alias wins, as in the scan. */
            while (table[at].units && !(table[at].count == count && memcmp(table[at].units, units, (size_t)count * 2) == 0)) at = (at + 1) & (capacity - 1);
            if (table[at].units) free(units);
            else table[at] = (member_name){units, count, i};
            p = end ? end + 1 : NULL;
        }
    for (uint32_t i = 0; i < c->lookup_capacity; i++) free(c->lookup[i].units);
    free(c->lookup);
    c->lookup = table; c->lookup_capacity = capacity; c->lookup_members = c->member_count;
}

static void index_library(aqua_library *library) {
    for (uint32_t i = 0; i < host_registry.class_count; i++) if (host_registry.classes[i]->library == library) index_class(host_registry.classes[i]);
}

int32_t class_member_units(const aqua_class *c, const uint16_t *units, uint32_t count) {
    if (!c->lookup || c->lookup_members != c->member_count) {
        size_t length;
        char *name = utf16_to_utf8(units, count, &length);
        int32_t index = class_member(c, name);
        free(name);
        return index;
    }
    for (uint32_t at = units_hash(units, count) & (c->lookup_capacity - 1);; at = (at + 1) & (c->lookup_capacity - 1)) {
        const member_name *e = &c->lookup[at];
        if (!e->units) return -1;
        if (e->count == count && memcmp(e->units, units, (size_t)count * 2) == 0) return (int32_t)e->member;
    }
}

aqua_library *registry_add(const char *name, void (*entry)(aqua_library *)) {
    aqua_library *library = new_library(name);
    entry(library);
    index_library(library);
    return library;
}

aqua_library *registry_add_external(const char *name, uint32_t (*entry)(aqua_library *, uint32_t)) {
    aqua_library *library = new_library(name);
    if (entry(library, AQUA_API_VERSION) == 0) return NULL;
    index_library(library);
    return library;
}

/* The library's name from its file: aqua_sqlite.dll and libaqua_sqlite.so are "sqlite". */
static char *library_name(const char *path) {
    const char *base = path;
    for (const char *p = path; *p; p++) if (*p == '/' || *p == '\\') base = p + 1;
    if (strncmp(base, "lib", 3) == 0) base += 3;
    if (strncmp(base, "aqua_", 5) == 0) base += 5;
    size_t length = strcspn(base, ".");
    char *name = malloc(length + 1);
    memcpy(name, base, length);
    name[length] = 0;
    return name;
}

bool library_load(const char *path, char **error) {
    registry_ensure();
    void *handle = platform_library_open(path, error);
    if (!handle) return false;
    uint32_t (*entry)(aqua_library *, uint32_t) = (uint32_t (*)(aqua_library *, uint32_t))platform_library_symbol(handle, "aqua_library_entry");
    char *name = library_name(path);
    bool loaded = entry && registry_add_external(name, entry);
    if (!loaded && error) {
        char message[1024];
        snprintf(message, sizeof message, entry ? "%s does not support this runtime (library API %d)." : "%s is not an AquariusLang library (no aqua_library_entry).", path, AQUA_API_VERSION);
        *error = strdup(message);
    }
    free(name);
    return loaded;
}

static aqua_class *new_class(aqua_library *library, const char *names, bool module, void (*finalize)(aqua_runtime *, void *)) {
    aqua_class *c = calloc(1, sizeof *c);
    c->library = library; c->names = copy(names); c->module = module; c->finalize = finalize;
    c->index = host_registry.class_count;
    host_registry.classes = realloc(host_registry.classes, (host_registry.class_count + 1) * sizeof *host_registry.classes);
    host_registry.classes[host_registry.class_count++] = c;
    return c;
}

aqua_class *aqua_define_module(aqua_library *library, const char *names) { return new_class(library, names, true, NULL); }
aqua_class *aqua_define_class(aqua_library *library, const char *name, void (*finalize)(aqua_runtime *, void *)) {
    return new_class(library, name, false, finalize);
}

aqua_library *registry_library(const char *name) {
    for (uint32_t i = 0; i < host_registry.library_count; i++) if (strcmp(host_registry.libraries[i]->name, name) == 0) return host_registry.libraries[i];
    return NULL;
}

aqua_class *registry_module(const char *name) {
    for (uint32_t i = 0; i < host_registry.class_count; i++) {
        aqua_class *c = host_registry.classes[i];
        if (c->module && names_contain(c->names, name)) return c;
    }
    return NULL;
}

static member *add_member(aqua_class *owner, const char *names, member_kind kind) {
    if (owner->member_count == owner->member_capacity) {
        owner->member_capacity = owner->member_capacity ? owner->member_capacity * 2 : 16;
        owner->members = realloc(owner->members, owner->member_capacity * sizeof *owner->members);
    }
    member *m = &owner->members[owner->member_count++];
    memset(m, 0, sizeof *m);
    m->kind = kind; m->names = copy(names);
    return m;
}

/* Arity of a signature: kinds before '|' are required; a '*' makes the count unbounded. */
static void signature_arity(const char *signature, uint32_t *minimum, uint32_t *maximum) {
    uint32_t count = 0, required = 0; bool optional = false, unbounded = false;
    for (const char *p = signature; *p; p++) {
        if (*p == '|') { optional = true; continue; }
        if (*p == '*') { unbounded = true; if (!optional && required) required--; count--; continue; }
        count++;
        if (!optional) required++;
    }
    *minimum = required;
    *maximum = unbounded ? UINT32_MAX : count;
}

void aqua_define_function(aqua_class *owner, const char *names, aqua_function function, const char *signature) {
    member *m = add_member(owner, names, MEMBER_FUNCTION);
    m->function = function; m->signature = copy(signature ? signature : "v*");
    signature_arity(m->signature, &m->minimum, &m->maximum);
}
void aqua_define_property(aqua_class *owner, const char *names, aqua_function getter) {
    add_member(owner, names, MEMBER_PROPERTY)->function = getter;
}
void aqua_define_int(aqua_class *owner, const char *names, int32_t value) { add_member(owner, names, MEMBER_INT)->number = value; }
void aqua_define_double(aqua_class *owner, const char *names, double value) { add_member(owner, names, MEMBER_DOUBLE)->number = value; }
void aqua_define_bool(aqua_class *owner, const char *names, bool value) { add_member(owner, names, MEMBER_BOOL)->number = value ? 1 : 0; }
void aqua_define_string(aqua_class *owner, const char *names, const char *utf8) { add_member(owner, names, MEMBER_STRING)->text = copy(utf8); }

void aqua_library_state(aqua_library *library, void *(*create)(aqua_runtime *), void (*destroy)(aqua_runtime *, void *)) {
    library->create_state = create; library->destroy_state = destroy;
}

int32_t class_member(const aqua_class *c, const char *name) {
    for (uint32_t i = 0; i < c->member_count; i++) if (names_contain(c->members[i].names, name)) return (int32_t)i;
    return -1;
}

void class_define_value(aqua_class *owner, const char *names, aqua_value value) { add_member(owner, names, MEMBER_VALUE)->value = value; }

void aqua_define_async(aqua_class *owner, const char *names, aqua_function function, const char *signature) {
    aqua_define_function(owner, names, function, signature);
    owner->members[owner->member_count - 1].async = true;
}
