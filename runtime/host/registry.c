/* The process-wide registry of libraries, their modules and classes, and each class's members. Registration happens
   once per library (default libraries at startup, external ones when loaded); runtimes create module instances and
   per-runtime state lazily. */
#include "host.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

registry host_registry;
extern const aqua_api_table host_api_table;

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

aqua_library *registry_add(const char *name, void (*entry)(aqua_library *)) {
    aqua_library *library = new_library(name);
    entry(library);
    return library;
}

aqua_library *registry_add_external(const char *name, uint32_t (*entry)(aqua_library *, uint32_t)) {
    aqua_library *library = new_library(name);
    if (entry(library, AQUA_API_VERSION) == 0) return NULL;
    return library;
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
